namespace Deep.Client.Maui.Services;

/// <summary>Closed display-only failures; private transport messages remain
/// available as inner exceptions, never as UI or evidence text.</summary>
internal static class Did2NetworkIoFailure
{
    internal static InvalidOperationException AtStage(string stage, IOException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (stage is not ("AccountProof" or "NetworkVerification" or
            "PreKeyStaging" or "PreKeyPublication"))
            throw new ArgumentException("Unknown DID2 diagnostic stage.", nameof(stage));

        return new InvalidOperationException(
            $"DID2 {stage} failed (TransportIo).", exception);
    }
}
