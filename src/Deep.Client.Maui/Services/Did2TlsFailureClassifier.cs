using System.Security.Authentication;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace Deep.Client.Maui.Services;

/// <summary>Display-only, closed TLS classifications. Never copies exception
/// messages, certificate names, URLs or request bytes into UI/evidence.</summary>
internal static class Did2TlsFailureClassifier
{
    private static readonly string[] KnownStatuses = ["Revoked", "NotTimeValid",
        "UntrustedRoot", "PartialChain", "RevocationStatusUnknown", "OfflineRevocation",
        "RemoteCertificateNameMismatch", "RemoteCertificateNotAvailable"];

    internal static string Classify(Exception exception)
    {
        var statuses = new HashSet<string>(StringComparer.Ordinal);
        Exception? current = exception;
        for (var depth = 0; current is not null && depth < 8; depth++, current = current.InnerException)
        {
            if (current is not AuthenticationException || current.Message.Length > 4096) continue;
            foreach (var status in KnownStatuses)
                if (Regex.IsMatch(current.Message, @"\b" + status + @"\b", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
                    statuses.Add(status);
        }
        return statuses.Count == 0 ? "Unknown" : string.Join(" ", KnownStatuses.Where(statuses.Contains));
    }
    internal static bool ObserveAndReject(SslPolicyErrors errors,
        X509ChainStatusFlags chainFlags, Action<string> report)
    {
        var labels = new List<string>();
        foreach (var name in KnownStatuses)
            if (Enum.TryParse<X509ChainStatusFlags>(name, out var flag) &&
                flag != X509ChainStatusFlags.NoError && (chainFlags & flag) != 0)
                labels.Add(name);
        if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
            labels.Add("RemoteCertificateNameMismatch");
        if ((errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0)
            labels.Add("RemoteCertificateNotAvailable");
        report(labels.Count > 0 ? string.Join(" ", labels) :
            errors == SslPolicyErrors.None && chainFlags == X509ChainStatusFlags.NoError
                ? "PlatformChainAccepted" : "OtherChainError");
        // Never accepts a certificate, even when platform validation succeeds.
        // This separate diagnostic connection sends no application request.
        return false;
    }

    internal static async Task<string> ObserveRejectedHandshakeAsync(Uri origin,
        CancellationToken cancellationToken)
    {
        if (!origin.IsAbsoluteUri || origin.Scheme != Uri.UriSchemeHttps ||
            !origin.IsDefaultPort || origin.IsLoopback || origin.AbsolutePath != "/" ||
            origin.Query.Length != 0 || origin.Fragment.Length != 0 ||
            origin.UserInfo.Length != 0 || origin.HostNameType != UriHostNameType.Dns)
            throw new ArgumentException("TLS diagnostic requires the canonical public HTTPS origin.", nameof(origin));
        cancellationToken.ThrowIfCancellationRequested();
        var observed = "NoPeerCertificate";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var socket = new TcpClient();
        try
        {
            await socket.ConnectAsync(origin.DnsSafeHost, origin.Port, deadline.Token).ConfigureAwait(false);
            using var tls = new SslStream(socket.GetStream(), leaveInnerStreamOpen: false,
                (_, _, chain, errors) => ObserveAndReject(errors,
                    chain?.ChainStatus.Aggregate(X509ChainStatusFlags.NoError,
                        (flags, status) => flags | status.Status) ?? X509ChainStatusFlags.NoError,
                    value => observed = value + ClassifyRevocationDetail(chain?.ChainStatus ?? [])));
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = origin.DnsSafeHost,
                AllowTlsResume = false,
                CertificateRevocationCheckMode = X509RevocationMode.Online
            }, deadline.Token).ConfigureAwait(false);
        }
        catch (AuthenticationException) { return observed; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return "DiagnosticTimeout"; }
        catch (IOException) { return observed; }
        catch (SocketException) { return "DiagnosticTransportFailure"; }
        return observed;
    }

    internal static string ClassifyRevocationDetail(IEnumerable<X509ChainStatus> statuses)
    {
        foreach (var status in statuses)
        {
            if ((status.Status & X509ChainStatusFlags.RevocationStatusUnknown) == 0 ||
                status.StatusInformation is not { Length: > 0 and <= 4096 } detail) continue;
            if (detail.Contains("cleartext", StringComparison.OrdinalIgnoreCase))
                return " CleartextBlocked";
            if (detail.Contains("No CRLs found", StringComparison.OrdinalIgnoreCase) ||
                detail.Contains("no valid CRL found", StringComparison.OrdinalIgnoreCase))
                return " CrlUnavailable";
            if (detail.Contains("Could not determine revocation status", StringComparison.OrdinalIgnoreCase))
                return " RevocationUndetermined";
        }
        return string.Empty;
    }
}
