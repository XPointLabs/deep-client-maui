using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Deep.Client.Maui.UiTests;

internal sealed partial record PhysicalAckCorrelationEvidence(
    string MarkerHash,
    string CorrelationHash,
    string State,
    int AttemptCount)
{
    internal static PhysicalAckCorrelationEvidence ParseExact(string value)
    {
        var match = ExactPattern().Match(value);
        if (!match.Success)
            throw new InvalidDataException("Physical ACK correlation evidence is not canonical.");
        var state = match.Groups["state"].Value;
        var attempts = int.Parse(
            match.Groups["attempts"].Value,
            System.Globalization.CultureInfo.InvariantCulture);
        if ((state == "ambiguous-attempted" && attempts != 1)
            || (state == "recovered-durable" && attempts != 2))
            throw new InvalidDataException("Physical ACK correlation state/count is invalid.");
        return new PhysicalAckCorrelationEvidence(
            match.Groups["marker"].Value,
            match.Groups["correlation"].Value,
            state,
            attempts);
    }

    internal static string MarkerHashFor(string marker) =>
        DomainHash("deep.physical-e2e.ack-marker.v1\0", marker);

    internal string SanitizedEvidenceHash() =>
        DomainHash("deep.physical-e2e.ack-correlation-evidence.v1\0", CorrelationHash);

    internal string CanonicalValue() =>
        $"v1|marker={MarkerHash}|correlation={CorrelationHash}|state={State}|attempts={AttemptCount}";

    private static string DomainHash(string domain, string value)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.ASCII.GetBytes(domain));
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    [GeneratedRegex(
        "^v1\\|marker=(?<marker>[a-f0-9]{64})\\|correlation=(?<correlation>[a-f0-9]{64})\\|state=(?<state>ambiguous-attempted|recovered-durable)\\|attempts=(?<attempts>[12])$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ExactPattern();
}
