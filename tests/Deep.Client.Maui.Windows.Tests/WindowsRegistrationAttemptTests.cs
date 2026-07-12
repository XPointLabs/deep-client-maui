namespace Deep.Client.Maui.Windows.Tests;

public sealed class WindowsRegistrationAttemptTests
{
    [Fact]
    public void FailedRegistrationCleansUpAndTheNextAttemptCanSucceed()
    {
        var candidate = new FakeRegistration();
        var failures = new List<Exception>();

        var first = WindowsRegistrationAttempt.TryRegister(
            () => candidate,
            registration => registration.HandlerCount++,
            registration =>
            {
                registration.RegisterAttempts++;
                throw new InvalidOperationException("transient registration failure");
            },
            registration => registration.HandlerCount--,
            registration => registration.UnregisterAttempts++,
            failures.Add);

        Assert.Null(first);
        Assert.Equal(0, candidate.HandlerCount);
        Assert.Equal(1, candidate.RegisterAttempts);
        Assert.Equal(1, candidate.UnregisterAttempts);
        Assert.Single(failures);

        var second = WindowsRegistrationAttempt.TryRegister(
            () => candidate,
            registration => registration.HandlerCount++,
            registration => registration.RegisterAttempts++,
            registration => registration.HandlerCount--,
            registration => registration.UnregisterAttempts++,
            failures.Add);

        Assert.Same(candidate, second);
        Assert.Equal(1, candidate.HandlerCount);
        Assert.Equal(2, candidate.RegisterAttempts);
        Assert.Equal(1, candidate.UnregisterAttempts);
        Assert.Single(failures);
    }

    [Fact]
    public void HandlerAttachFailureStillAttemptsHandlerCleanup()
    {
        var candidate = new FakeRegistration();

        var result = WindowsRegistrationAttempt.TryRegister(
            () => candidate,
            registration =>
            {
                registration.HandlerCount++;
                throw new InvalidOperationException("event source failure");
            },
            registration => registration.RegisterAttempts++,
            registration => registration.HandlerCount--,
            registration => registration.UnregisterAttempts++,
            static _ => { });

        Assert.Null(result);
        Assert.Equal(0, candidate.HandlerCount);
        Assert.Equal(0, candidate.RegisterAttempts);
        Assert.Equal(0, candidate.UnregisterAttempts);
    }

    private sealed class FakeRegistration
    {
        public int HandlerCount { get; set; }

        public int RegisterAttempts { get; set; }

        public int UnregisterAttempts { get; set; }
    }
}
