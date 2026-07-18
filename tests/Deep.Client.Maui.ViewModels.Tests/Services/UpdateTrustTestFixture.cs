using Deep.Client.Maui.Core.Services;
using Sodium;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

internal sealed class UpdateTrustTestFixture : IDisposable
{
    internal const string TargetPath = "android/network.xpoint.deep-test.apk";
    internal const string PackageId = "network.xpoint.deep";
    internal const string PackageSignerSha256 =
        "67c86fcab94cd7bf00185fef40bdf39e213dd82c260e0c8958e6ffcdbe438ed0";
    internal const string VersionName = "2.0.1-test";
    internal const string VersionCode = "20001";
    internal static readonly DateTimeOffset UpdateStart =
        new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly Dictionary<string, TestKey> keys;
    private readonly RoleKeySet oldRoles;
    private readonly RoleKeySet newRoles;
    private readonly TestKey[] delegated;

    public UpdateTrustTestFixture()
    {
        keys = new[]
        {
            "old-root-a", "old-root-b", "old-root-c",
            "new-root-a", "new-root-b", "new-root-c",
            "old-timestamp", "old-snapshot", "old-targets-a", "old-targets-b",
            "new-timestamp", "new-snapshot", "new-targets-a", "new-targets-b",
            "delegated-a", "delegated-b", "delegated-c"
        }.Select(CreateKey).ToDictionary(key => key.Name, StringComparer.Ordinal);

        oldRoles = new RoleKeySet(
            Root: Select("old-root-a", "old-root-b", "old-root-c"),
            Timestamp: Select("old-timestamp"),
            Snapshot: Select("old-snapshot"),
            Targets: Select("old-targets-a", "old-targets-b"));
        newRoles = new RoleKeySet(
            Root: Select("new-root-a", "new-root-b", "new-root-c"),
            Timestamp: Select("new-timestamp"),
            Snapshot: Select("new-snapshot"),
            Targets: Select("new-targets-a", "new-targets-b"));
        delegated = Select("delegated-a", "delegated-b", "delegated-c");

        ApkBytes = Encoding.UTF8.GetBytes(
            "Deep P02B PUBLIC SYNTHETIC APK FIXTURE ONLY; not an installable Android package.");
        RootOne = BuildRoot(1, oldRoles, oldRoles.Root.Take(2).ToArray());
        RootTwo = BuildRoot(
            2,
            newRoles,
            oldRoles.Root.Take(2).Concat(newRoles.Root.Take(2)).ToArray());
        RootTwoOldThresholdOnly = BuildRoot(2, newRoles, oldRoles.Root.Take(2).ToArray());
    }

    public byte[] ApkBytes { get; }

    public byte[] RootOne { get; }

    public byte[] RootTwo { get; }

    public byte[] RootTwoOldThresholdOnly { get; }

    public UpdateMetadataBundle BuildBundle(
        bool rotated = false,
        int onlineVersion = 1,
        string expiry = "2030-01-03T00:00:00Z",
        bool revokedTimestampSigner = false,
        bool oldThresholdOnlyCandidate = false)
    {
        var roles = rotated ? newRoles : oldRoles;
        var root = RootOne;
        IReadOnlyList<ReadOnlyMemory<byte>> candidates = rotated
            ? new ReadOnlyMemory<byte>[]
            {
                oldThresholdOnlyCandidate ? RootTwoOldThresholdOnly : RootTwo
            }
            : Array.Empty<ReadOnlyMemory<byte>>();

        var delegatedSigned = SignedCommon("targets", onlineVersion, expiry);
        delegatedSigned["targets"] = new JsonObject
        {
            [TargetPath] = new JsonObject
            {
                ["length"] = ApkBytes.Length,
                ["hashes"] = new JsonObject { ["sha256"] = Sha256(ApkBytes) },
                ["custom"] = new JsonObject
                {
                    ["platform"] = "android",
                    ["packageId"] = PackageId,
                    ["packageSignerSha256"] = PackageSignerSha256,
                    ["versionCode"] = VersionCode,
                    ["versionName"] = VersionName,
                    ["sourceCommit"] = "9dc1502392ce2c1a86441df6308f2db54410eae8"
                }
            }
        };
        var delegatedBytes = Sign(delegatedSigned, delegated.Take(2).ToArray());

        var targetsSigned = SignedCommon("targets", onlineVersion, expiry);
        targetsSigned["targets"] = new JsonObject();
        targetsSigned["delegations"] = new JsonObject
        {
            ["keys"] = KeyObject(delegated),
            ["roles"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "android-release",
                    ["keyids"] = StringArray(delegated.Select(key => key.KeyId)),
                    ["threshold"] = 2,
                    ["terminating"] = true,
                    ["paths"] = new JsonArray("android/*")
                }
            }
        };
        var targetsBytes = Sign(targetsSigned, roles.Targets);

        var snapshotSigned = SignedCommon("snapshot", onlineVersion, expiry);
        snapshotSigned["meta"] = new JsonObject
        {
            ["targets.json"] = Meta(onlineVersion, targetsBytes),
            ["android-release.json"] = Meta(onlineVersion, delegatedBytes)
        };
        var snapshotBytes = Sign(snapshotSigned, roles.Snapshot);

        var timestampSigned = SignedCommon("timestamp", onlineVersion, expiry);
        timestampSigned["meta"] = new JsonObject
        {
            ["snapshot.json"] = Meta(onlineVersion, snapshotBytes)
        };
        var timestampSigners = revokedTimestampSigner
            ? oldRoles.Timestamp
            : roles.Timestamp;
        var timestampBytes = Sign(timestampSigned, timestampSigners);

        return new UpdateMetadataBundle(
            root,
            candidates,
            timestampBytes,
            snapshotBytes,
            targetsBytes,
            delegatedBytes);
    }

    public byte[] BuildDelegatedOnly(int version)
    {
        var signed = SignedCommon("targets", version, "2030-01-03T00:00:00Z");
        signed["targets"] = new JsonObject
        {
            [TargetPath] = new JsonObject
            {
                ["length"] = ApkBytes.Length,
                ["hashes"] = new JsonObject { ["sha256"] = Sha256(ApkBytes) },
                ["custom"] = new JsonObject
                {
                    ["platform"] = "android",
                    ["packageId"] = PackageId,
                    ["packageSignerSha256"] = PackageSignerSha256,
                    ["versionCode"] = VersionCode,
                    ["versionName"] = VersionName,
                    ["sourceCommit"] = "9dc1502392ce2c1a86441df6308f2db54410eae8"
                }
            }
        };
        return Sign(signed, delegated.Take(2).ToArray());
    }

    public void Dispose()
    {
        foreach (var key in keys.Values)
        {
            key.Pair.Dispose();
        }
    }

    private TestKey[] Select(params string[] names) =>
        names.Select(name => keys[name]).ToArray();

    private static TestKey CreateKey(string name)
    {
        var seed = SHA256.HashData(Encoding.UTF8.GetBytes($"Deep P02B insecure test key:{name}"));
        var pair = PublicKeyAuth.GenerateKeyPair(seed);
        var metadata = new JsonObject
        {
            ["keytype"] = "ed25519",
            ["scheme"] = "ed25519",
            ["keyval"] = new JsonObject
            {
                ["public"] = Convert.ToHexString(pair.PublicKey).ToLowerInvariant()
            }
        };
        var keyBytes = Canonical(metadata);
        return new TestKey(name, Sha256(keyBytes), pair, metadata);
    }

    private byte[] BuildRoot(int version, RoleKeySet roleSet, TestKey[] signers)
    {
        var allKeys = roleSet.Root
            .Concat(roleSet.Timestamp)
            .Concat(roleSet.Snapshot)
            .Concat(roleSet.Targets)
            .DistinctBy(key => key.KeyId)
            .ToArray();
        var signed = SignedCommon("root", version, "2035-01-01T00:00:00Z");
        signed["consistent_snapshot"] = true;
        signed["keys"] = KeyObject(allKeys);
        signed["roles"] = new JsonObject
        {
            ["root"] = Role(roleSet.Root, 2),
            ["timestamp"] = Role(roleSet.Timestamp, 1),
            ["snapshot"] = Role(roleSet.Snapshot, 1),
            ["targets"] = Role(roleSet.Targets, 2)
        };
        return Sign(signed, signers);
    }

    private static JsonObject SignedCommon(string type, int version, string expiry) =>
        new()
        {
            ["_type"] = type,
            ["spec_version"] = "1.0.35",
            ["version"] = version,
            ["expires"] = expiry
        };

    private static JsonObject KeyObject(IEnumerable<TestKey> selected)
    {
        var result = new JsonObject();
        foreach (var key in selected)
        {
            result[key.KeyId] = key.Metadata.DeepClone();
        }
        return result;
    }

    private static JsonObject Role(IEnumerable<TestKey> selected, int threshold) =>
        new()
        {
            ["keyids"] = StringArray(selected.Select(key => key.KeyId)),
            ["threshold"] = threshold
        };

    private static JsonArray StringArray(IEnumerable<string> values)
    {
        var result = new JsonArray();
        foreach (var value in values)
        {
            result.Add(value);
        }
        return result;
    }

    private static JsonObject Meta(int version, byte[] raw) =>
        new()
        {
            ["version"] = version,
            ["length"] = raw.Length,
            ["hashes"] = new JsonObject { ["sha256"] = Sha256(raw) }
        };

    private static byte[] Sign(JsonObject signed, IReadOnlyCollection<TestKey> signers)
    {
        var payload = Canonical(signed);
        var signatures = new JsonArray();
        foreach (var signer in signers)
        {
            signatures.Add(new JsonObject
            {
                ["keyid"] = signer.KeyId,
                ["sig"] = Convert.ToHexString(
                    PublicKeyAuth.SignDetached(payload, signer.Pair.PrivateKey))
                    .ToLowerInvariant()
            });
        }
        return Canonical(new JsonObject
        {
            ["signatures"] = signatures,
            ["signed"] = signed.DeepClone()
        });
    }

    private static byte[] Canonical(JsonNode node)
    {
        var bytes = Encoding.UTF8.GetBytes(node.ToJsonString(JsonOptions));
        return PortableUpdateMetadataVerifier.Canonicalize(bytes);
    }

    private static string Sha256(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private sealed record TestKey(
        string Name,
        string KeyId,
        KeyPair Pair,
        JsonObject Metadata);

    private sealed record RoleKeySet(
        TestKey[] Root,
        TestKey[] Timestamp,
        TestKey[] Snapshot,
        TestKey[] Targets);
}
