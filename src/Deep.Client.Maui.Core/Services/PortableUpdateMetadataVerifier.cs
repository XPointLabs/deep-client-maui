using Sodium;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Deep.Client.Maui.Core.Services;

public sealed class PortableUpdateMetadataVerifier
{
    private const string SpecificationVersion = "1.0.35";
    private const int MaxMetadataBytes = 1_048_576;
    private const long MaxSafeInteger = 9_007_199_254_740_991;
    private static readonly JsonSerializerOptions StringOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static TrustedUpdateState CreateInitialState(ReadOnlyMemory<byte> trustedRoot)
    {
        var root = ParseDocument(trustedRoot, "root");
        ValidateRoot(root);
        return new TrustedUpdateState(
            TrustedUpdateState.CurrentSchema,
            RootBinding(root),
            new TrustedMetadataVersions(0, 0, 0, 0));
    }

    public async Task<VerifiedUpdateMetadata> VerifyAsync(
        UpdateMetadataBundle bundle,
        TrustedUpdateState trustedState,
        DateTimeOffset updateStartUtc,
        string targetPath,
        Func<TrustedUpdateState, CancellationToken, Task>? persistRotatedRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ValidateTrustedState(trustedState);
        Require(updateStartUtc.Offset == TimeSpan.Zero, "Update start time must be fixed UTC.");

        var trustedRoot = ParseDocument(bundle.TrustedRoot, "root");
        ValidateRoot(trustedRoot);
        var initialBinding = RootBinding(trustedRoot);
        Require(
            trustedState.TrustedRoot == initialBinding,
            "Startup trusted root does not exactly match persisted version and raw SHA-256.");

        var activeRoot = trustedRoot;
        foreach (var candidateBytes in bundle.CandidateRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = ParseDocument(candidateBytes, "root");
            ValidateRoot(candidate);
            Require(
                Version(candidate) == Version(activeRoot) + 1,
                "Root version must advance by exactly one.");

            var oldKeys = Signed(activeRoot).GetProperty("keys");
            var newKeys = Signed(candidate).GetProperty("keys");
            var unionKeys = MergeKeyObjects(oldKeys, newKeys);
            var oldRole = Signed(activeRoot).GetProperty("roles").GetProperty("root");
            var newRole = Signed(candidate).GetProperty("roles").GetProperty("root");
            var allowed = RoleKeyIds(oldRole).Concat(RoleKeyIds(newRole)).ToHashSet(StringComparer.Ordinal);

            VerifyRoleThreshold(candidate, oldRole, unionKeys, "new root old-root threshold", allowed);
            VerifyRoleThreshold(candidate, newRole, unionKeys, "new root new-root threshold", allowed);
            activeRoot = candidate;

            if (persistRotatedRoot is not null)
            {
                var provisional = trustedState with { TrustedRoot = RootBinding(activeRoot) };
                await persistRotatedRoot(provisional, cancellationToken);
            }
        }

        RequireNotExpired(activeRoot, updateStartUtc, "root");

        var timestamp = ParseDocument(bundle.Timestamp, "timestamp");
        VerifyTopLevel(timestamp, "timestamp", activeRoot, updateStartUtc);
        AssertNoRollback(
            Version(timestamp),
            trustedState.Versions.Timestamp,
            "timestamp",
            strictlyNew: true);
        var timestampMeta = Signed(timestamp).GetProperty("meta");
        var hasSnapshotBinding = timestampMeta.TryGetProperty(
            "snapshot.json",
            out var snapshotBinding);
        Require(timestampMeta.EnumerateObject().Count() == 1 && hasSnapshotBinding,
        "Timestamp must describe only snapshot.json.");
        AssertNoRollback(
            RequiredInt(snapshotBinding, "version", "timestamp snapshot reference"),
            trustedState.Versions.Snapshot,
            "timestamp snapshot reference");

        var snapshot = ParseDocument(bundle.Snapshot, "snapshot");
        VerifyTopLevel(snapshot, "snapshot", activeRoot, updateStartUtc);
        VerifyMetaBinding(snapshot, snapshotBinding, "snapshot");
        AssertNoRollback(
            Version(snapshot),
            trustedState.Versions.Snapshot,
            "snapshot");
        var snapshotMeta = Signed(snapshot).GetProperty("meta");
        var snapshotNames = snapshotMeta.EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Require(
            snapshotNames.SequenceEqual(new[] { "android-release.json", "targets.json" }),
            "Snapshot must describe exactly targets.json and android-release.json.");

        var targets = ParseDocument(bundle.Targets, "targets");
        VerifyTopLevel(targets, "targets", activeRoot, updateStartUtc);
        VerifyMetaBinding(
            targets,
            snapshotMeta.GetProperty("targets.json"),
            "targets");
        AssertNoRollback(
            Version(targets),
            trustedState.Versions.Targets,
            "targets");

        var androidRelease = ParseDocument(bundle.AndroidRelease, "targets");
        VerifyDelegatedTargets(
            androidRelease,
            targets,
            snapshot,
            updateStartUtc,
            trustedState.Versions.AndroidRelease);

        var androidTarget = ParseAndroidTarget(androidRelease, targetPath);
        var finalState = new TrustedUpdateState(
            TrustedUpdateState.CurrentSchema,
            RootBinding(activeRoot),
            new TrustedMetadataVersions(
                Version(timestamp),
                Version(snapshot),
                Version(targets),
                Version(androidRelease)));

        return new VerifiedUpdateMetadata(
            finalState,
            androidTarget,
            Version(activeRoot),
            Version(timestamp),
            Version(snapshot),
            Version(targets),
            Version(androidRelease));
    }

    public static void ValidateTrustedState(TrustedUpdateState? state)
    {
        Require(state is not null &&
            state.Schema == TrustedUpdateState.CurrentSchema &&
            state.TrustedRoot.Version >= 1 &&
            IsLowerHex(state.TrustedRoot.Sha256, 64) &&
            state.Versions.Timestamp >= 0 &&
            state.Versions.Snapshot >= 0 &&
            state.Versions.Targets >= 0 &&
            state.Versions.AndroidRelease >= 0,
        "Persisted update-trust state is invalid.");
    }

    public static byte[] Canonicalize(ReadOnlyMemory<byte> json)
    {
        using var document = ParseStrict(json, "canonical JSON");
        return Encoding.UTF8.GetBytes(CanonicalJson(document.RootElement));
    }

    private static MetadataDocument ParseDocument(ReadOnlyMemory<byte> raw, string expectedType)
    {
        Require(raw.Length <= MaxMetadataBytes, "Metadata exceeds the 1 MiB limit.");
        using var json = ParseStrict(raw, expectedType);
        var canonical = Encoding.UTF8.GetBytes(CanonicalJson(json.RootElement));
        Require(raw.Span.SequenceEqual(canonical),
            $"{expectedType} raw bytes are not the exact canonical POUF encoding.");
        var envelope = json.RootElement.Clone();
        Require(envelope.ValueKind == JsonValueKind.Object &&
            envelope.TryGetProperty("signed", out var signed) &&
            signed.ValueKind == JsonValueKind.Object,
        $"{expectedType} envelope is invalid.");
        ValidateCommon(envelope, expectedType);
        return new MetadataDocument(raw.ToArray(), envelope);
    }

    private static JsonDocument ParseStrict(ReadOnlyMemory<byte> raw, string label)
    {
        try
        {
            var document = JsonDocument.Parse(raw, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
            ValidateNoDuplicateKeys(document.RootElement, label);
            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{label} is invalid JSON.", exception);
        }
    }

    private static void ValidateNoDuplicateKeys(JsonElement element, string label)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                Require(keys.Add(property.Name), $"{label} contains duplicate object key.");
                ValidateNoDuplicateKeys(property.Value, label);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ValidateNoDuplicateKeys(item, label);
            }
        }
    }

    private static string CanonicalJson(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => "null",
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.String => JsonSerializer.Serialize(element.GetString(), StringOptions),
            JsonValueKind.Number => CanonicalInteger(element),
            JsonValueKind.Array => $"[{string.Join(",", element.EnumerateArray().Select(CanonicalJson))}]",
            JsonValueKind.Object => "{" + string.Join(
                ",",
                element.EnumerateObject()
                    .OrderBy(property => property.Name, StringComparer.Ordinal)
                    .Select(property =>
                        $"{JsonSerializer.Serialize(property.Name, StringOptions)}:{CanonicalJson(property.Value)}")) + "}",
            _ => throw new InvalidDataException("Canonical metadata contains an unsupported value.")
        };
    }

    private static string CanonicalInteger(JsonElement element)
    {
        Require(element.TryGetInt64(out var value) &&
            value >= -MaxSafeInteger &&
            value <= MaxSafeInteger,
        "Canonical metadata permits only safe integers.");
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static void ValidateCommon(JsonElement envelope, string expectedType)
    {
        var signed = envelope.GetProperty("signed");
        Require(RequiredString(signed, "_type", expectedType) == expectedType,
            $"Expected {expectedType} metadata.");
        Require(RequiredString(signed, "spec_version", expectedType) == SpecificationVersion,
            $"{expectedType} spec version is unsupported.");
        Require(RequiredInt(signed, "version", expectedType) >= 1,
            $"{expectedType} version is invalid.");
        _ = RequiredExpiry(signed, expectedType);
        var signatures = envelope.GetProperty("signatures");
        Require(signatures.ValueKind == JsonValueKind.Array &&
            signatures.GetArrayLength() > 0,
        $"{expectedType} signatures are missing.");
        var signerIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var signature in signatures.EnumerateArray())
        {
            var keyId = RequiredString(signature, "keyid", expectedType);
            var value = RequiredString(signature, "sig", expectedType);
            Require(IsLowerHex(keyId, 64) && IsLowerHex(value, 128),
                $"{expectedType} signature encoding is invalid.");
            Require(signerIds.Add(keyId), $"{expectedType} contains duplicate signer ids.");
        }
    }

    private static void ValidateRoot(MetadataDocument root)
    {
        var signed = Signed(root);
        Require(signed.GetProperty("consistent_snapshot").ValueKind == JsonValueKind.True,
            "Consistent snapshots must be enabled.");
        var keys = signed.GetProperty("keys");
        var roles = signed.GetProperty("roles");
        Require(keys.ValueKind == JsonValueKind.Object && roles.ValueKind == JsonValueKind.Object,
            "Root keys/roles are invalid.");
        foreach (var property in keys.EnumerateObject())
        {
            ValidateKey(property.Value);
            Require(IsLowerHex(property.Name, 64) &&
                Sha256Hex(Encoding.UTF8.GetBytes(CanonicalJson(property.Value))) == property.Name,
            "Root key id does not match canonical key bytes.");
        }

        foreach (var roleName in new[] { "root", "targets", "snapshot", "timestamp" })
        {
            ValidateRole(roles.GetProperty(roleName), keys, roleName);
        }
    }

    private static void ValidateKey(JsonElement key)
    {
        Require(
            RequiredString(key, "keytype", "key") == "ed25519" &&
            RequiredString(key, "scheme", "key") == "ed25519" &&
            key.GetProperty("keyval").TryGetProperty("public", out var publicKey) &&
            publicKey.ValueKind == JsonValueKind.String &&
            IsLowerHex(publicKey.GetString(), 64),
            "Only canonical Ed25519 public keys are supported.");
    }

    private static void ValidateRole(JsonElement role, JsonElement keys, string label)
    {
        var keyIds = RoleKeyIds(role);
        var threshold = RequiredInt(role, "threshold", label);
        Require(keyIds.Count > 0 && threshold >= 1 && threshold <= keyIds.Count,
            $"{label} role definition is invalid.");
        Require(keyIds.Distinct(StringComparer.Ordinal).Count() == keyIds.Count,
            $"{label} role repeats key ids.");
        foreach (var keyId in keyIds)
        {
            Require(IsLowerHex(keyId, 64) && keys.TryGetProperty(keyId, out _),
                $"{label} references an unknown key.");
        }
    }

    private static List<string> RoleKeyIds(JsonElement role)
    {
        JsonElement keyIds = default;
        var hasKeyIds = role.ValueKind == JsonValueKind.Object &&
            role.TryGetProperty("keyids", out keyIds);
        Require(hasKeyIds && keyIds.ValueKind == JsonValueKind.Array,
        "Role definition is invalid.");
        return keyIds.EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty)
            .ToList();
    }

    private static void VerifyRoleThreshold(
        MetadataDocument document,
        JsonElement role,
        JsonElement keys,
        string label,
        IReadOnlySet<string>? allowedIds = null)
    {
        var roleIds = RoleKeyIds(role).ToHashSet(StringComparer.Ordinal);
        var allowed = allowedIds ?? roleIds;
        var payload = Encoding.UTF8.GetBytes(CanonicalJson(Signed(document)));
        var valid = 0;
        foreach (var signature in document.Envelope.GetProperty("signatures").EnumerateArray())
        {
            var keyId = signature.GetProperty("keyid").GetString()!;
            Require(allowed.Contains(keyId), $"{label} contains an unknown signer.");
            if (!roleIds.Contains(keyId))
            {
                continue;
            }

            Require(keys.TryGetProperty(keyId, out var key), $"{label} signer key is absent.");
            ValidateKey(key);
            var publicKey = Convert.FromHexString(key.GetProperty("keyval").GetProperty("public").GetString()!);
            var signatureBytes = Convert.FromHexString(signature.GetProperty("sig").GetString()!);
            if (PublicKeyAuth.VerifyDetached(signatureBytes, payload, publicKey))
            {
                valid++;
            }
        }

        Require(valid >= RequiredInt(role, "threshold", label),
            $"{label} signature threshold is not met.");
    }

    private static void VerifyTopLevel(
        MetadataDocument document,
        string type,
        MetadataDocument root,
        DateTimeOffset updateStartUtc)
    {
        var rootSigned = Signed(root);
        var role = rootSigned.GetProperty("roles").GetProperty(type);
        VerifyRoleThreshold(document, role, rootSigned.GetProperty("keys"), type);
        RequireNotExpired(document, updateStartUtc, type);
    }

    private static void VerifyMetaBinding(
        MetadataDocument document,
        JsonElement expected,
        string label)
    {
        Require(RequiredInt(expected, "version", label) == Version(document),
            $"{label} version does not match parent metadata (possible mix-and-match attack).");
        Require(RequiredLong(expected, "length", label) == document.RawBytes.LongLength,
            $"{label} length does not match parent metadata (possible mix-and-match attack).");
        var expectedHash = expected.GetProperty("hashes").GetProperty("sha256").GetString();
        Require(IsLowerHex(expectedHash, 64) && expectedHash == Sha256Hex(document.RawBytes),
            $"{label} hash does not match parent metadata (possible mix-and-match attack).");
    }

    private static void VerifyDelegatedTargets(
        MetadataDocument document,
        MetadataDocument targets,
        MetadataDocument snapshot,
        DateTimeOffset updateStartUtc,
        int trustedVersion)
    {
        VerifyMetaBinding(
            document,
            Signed(snapshot).GetProperty("meta").GetProperty("android-release.json"),
            "android-release targets");
        AssertNoRollback(Version(document), trustedVersion, "android-release targets");
        RequireNotExpired(document, updateStartUtc, "android-release targets");

        var topLevelSigned = Signed(targets);
        var topLevelTargets = topLevelSigned.GetProperty("targets");
        Require(topLevelTargets.ValueKind == JsonValueKind.Object &&
            !topLevelTargets.EnumerateObject().Any(),
        "Top-level targets must delegate platform release paths.");
        var delegations = topLevelSigned.GetProperty("delegations");
        var keys = delegations.GetProperty("keys");
        var roles = delegations.GetProperty("roles");
        Require(keys.ValueKind == JsonValueKind.Object &&
            roles.ValueKind == JsonValueKind.Array &&
            roles.GetArrayLength() == 1,
        "Prototype permits exactly one platform release delegation.");
        foreach (var property in keys.EnumerateObject())
        {
            ValidateKey(property.Value);
            Require(Sha256Hex(Encoding.UTF8.GetBytes(CanonicalJson(property.Value))) == property.Name,
                "Delegated key id does not match canonical key bytes.");
        }

        var role = roles.EnumerateArray()
            .SingleOrDefault(item =>
                item.TryGetProperty("name", out var name) &&
                name.GetString() == "android-release");
        Require(role.ValueKind == JsonValueKind.Object &&
            role.GetProperty("terminating").ValueKind == JsonValueKind.True,
        "android-release delegation is missing or non-terminating.");
        ValidateRole(role, keys, "android-release");
        VerifyRoleThreshold(document, role, keys, "android-release targets");
        var patterns = role.GetProperty("paths").EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty)
            .ToArray();
        foreach (var target in Signed(document).GetProperty("targets").EnumerateObject())
        {
            Require(IsCanonicalTargetPath(target.Name),
                $"Delegated target path is non-canonical: {target.Name}.");
            Require(patterns.Any(pattern => PathMatches(pattern, target.Name)),
                $"Delegated target path is unauthorized: {target.Name}.");
        }
    }

    private static VerifiedAndroidTarget ParseAndroidTarget(
        MetadataDocument document,
        string targetPath)
    {
        Require(IsCanonicalTargetPath(targetPath), "Requested target path is non-canonical.");
        var signed = Signed(document);
        Require(signed.GetProperty("targets").TryGetProperty(targetPath, out var target),
            $"Target is not signed: {targetPath}.");
        var length = RequiredLong(target, "length", targetPath);
        Require(length >= 0, "Signed APK length is invalid.");
        var hash = target.GetProperty("hashes").GetProperty("sha256").GetString();
        Require(IsLowerHex(hash, 64), "Signed APK hash is invalid.");
        var custom = target.GetProperty("custom");
        Require(RequiredString(custom, "platform", targetPath) == "android",
            "Signed target is not Android.");
        var packageId = RequiredString(custom, "packageId", targetPath);
        var signer = RequiredString(custom, "packageSignerSha256", targetPath);
        Require(packageId == "network.xpoint.deep" && IsLowerHex(signer, 64),
            "Android package identity metadata is invalid.");
        return new VerifiedAndroidTarget(
            targetPath,
            length,
            hash!,
            packageId,
            signer,
            RequiredString(custom, "versionCode", targetPath),
            RequiredString(custom, "versionName", targetPath),
            RequiredString(custom, "sourceCommit", targetPath),
            RequiredExpiry(signed, "android-release targets"));
    }

    private static JsonElement MergeKeyObjects(JsonElement oldKeys, JsonElement newKeys)
    {
        var values = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in oldKeys.EnumerateObject())
        {
            values[property.Name] = property.Value.Clone();
        }
        foreach (var property in newKeys.EnumerateObject())
        {
            values[property.Name] = property.Value.Clone();
        }
        var json = "{" + string.Join(
            ",",
            values.Select(pair =>
                $"{JsonSerializer.Serialize(pair.Key, StringOptions)}:{CanonicalJson(pair.Value)}")) + "}";
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void RequireNotExpired(
        MetadataDocument document,
        DateTimeOffset updateStartUtc,
        string label) =>
        Require(RequiredExpiry(Signed(document), label) > updateStartUtc,
            $"{label} metadata is expired (possible freeze attack).");

    private static void AssertNoRollback(
        int version,
        int trustedVersion,
        string label,
        bool strictlyNew = false)
    {
        Require(trustedVersion >= 0, $"{label} trusted version is invalid.");
        Require(strictlyNew ? version > trustedVersion : version >= trustedVersion,
            $"{label} rollback detected.");
    }

    private static bool PathMatches(string pattern, string targetPath)
    {
        if (!pattern.Contains('*', StringComparison.Ordinal))
        {
            return pattern == targetPath;
        }
        Require(pattern.EndsWith('*') &&
            pattern.IndexOf('*') == pattern.Length - 1,
        "Only suffix-star delegation paths are supported.");
        return targetPath.StartsWith(pattern[..^1], StringComparison.Ordinal);
    }

    private static bool IsCanonicalTargetPath(string value) =>
        value.Split('/').All(part => part.Length > 0 && part is not "." and not "..");

    private static TrustedRootBinding RootBinding(MetadataDocument root) =>
        new(Version(root), Sha256Hex(root.RawBytes));

    private static JsonElement Signed(MetadataDocument document) =>
        document.Envelope.GetProperty("signed");

    private static int Version(MetadataDocument document) =>
        RequiredInt(Signed(document), "version", "metadata");

    private static string RequiredString(JsonElement value, string name, string label)
    {
        var text = value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
        Require(text is { Length: > 0 },
        $"{label} {name} is invalid.");
        return text;
    }

    private static int RequiredInt(JsonElement value, string name, string label)
    {
        var result = 0;
        var valid = value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty(name, out var property) &&
            property.TryGetInt32(out result);
        Require(valid && result >= 0,
        $"{label} {name} is invalid.");
        return result;
    }

    private static long RequiredLong(JsonElement value, string name, string label)
    {
        long result = 0;
        var valid = value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty(name, out var property) &&
            property.TryGetInt64(out result);
        Require(valid && result >= 0 && result <= MaxSafeInteger,
        $"{label} {name} is invalid.");
        return result;
    }

    private static DateTimeOffset RequiredExpiry(JsonElement signed, string label)
    {
        var value = RequiredString(signed, "expires", label);
        Require(DateTimeOffset.TryParseExact(
            value,
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var expiry),
        $"{label} expiry is invalid.");
        return expiry;
    }

    private static string Sha256Hex(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static bool IsLowerHex(string? value, int length) =>
        value is not null &&
        value.Length == length &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void Require(
        [DoesNotReturnIf(false)] bool condition,
        string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }

    private sealed record MetadataDocument(byte[] RawBytes, JsonElement Envelope);
}
