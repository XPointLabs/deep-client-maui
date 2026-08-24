namespace Deep.Client.Maui.UiTests;

public sealed class PhysicalAckCorrelationEvidenceTests
{
    [Fact]
    public void Exact_parser_rejects_cross_feed_and_noncanonical_states()
    {
        var marker = PhysicalAckCorrelationEvidence.MarkerHashFor("unique-marker");
        var correlation = new string('a', 64);
        var ambiguous = PhysicalAckCorrelationEvidence.ParseExact(
            $"v1|marker={marker}|correlation={correlation}|state=ambiguous-attempted|attempts=1");
        var recovered = PhysicalAckCorrelationEvidence.ParseExact(
            $"v1|marker={marker}|correlation={correlation}|state=recovered-durable|attempts=2");

        Assert.Equal(ambiguous.MarkerHash, recovered.MarkerHash);
        Assert.Equal(ambiguous.CorrelationHash, recovered.CorrelationHash);
        Assert.Matches("^[a-f0-9]{64}$", recovered.SanitizedEvidenceHash());
        Assert.Throws<InvalidDataException>(() => PhysicalAckCorrelationEvidence.ParseExact(
            $"v1|marker={new string('b', 64)}|correlation={correlation}|state=recovered-durable|attempts=1"));
        Assert.Throws<InvalidDataException>(() => PhysicalAckCorrelationEvidence.ParseExact(
            ambiguous.CanonicalValue() + "|raw=forbidden"));
    }
}
