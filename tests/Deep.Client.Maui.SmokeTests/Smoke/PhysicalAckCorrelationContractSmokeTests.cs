namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class PhysicalAckCorrelationContractSmokeTests
{
    [Fact]
    public void Physical_ack_surface_is_compile_guarded_lifecycle_bound_and_recycle_safe()
    {
        var root = FindRepositoryRoot();
        var provider = Read(root, "src", "Deep.Client.Maui", "Services",
            "PhysicalE2eAckCorrelationProvider.cs");
        var page = Read(root, "src", "Deep.Client.Maui", "Pages", "ChatPage.xaml.cs");
        var bootstrapper = Read(root, "src", "Deep.Client.Maui", "Services",
            "ClientRuntimeBootstrapper.cs");
        var composition = Read(root, "src", "Deep.Client.Maui", "Services",
            "PersistentClientRuntimeComposer.cs");
        var program = Read(root, "src", "Deep.Client.Maui", "MauiProgram.cs");

        Assert.StartsWith("#if DEBUG && DEEP_PHYSICAL_E2E", provider,
            StringComparison.Ordinal);
        Assert.EndsWith("#endif", provider.TrimEnd(), StringComparison.Ordinal);
        Assert.DoesNotContain("PhysicalE2eAckCorrelationProvider", program,
            StringComparison.Ordinal);
        Assert.Contains("PhysicalE2eAckCorrelationProvider.Bind(runtime)", composition,
            StringComparison.Ordinal);
        Assert.Contains("PhysicalE2eAckCorrelationProvider.Unbind(value)", bootstrapper,
            StringComparison.Ordinal);

        var clear = page.IndexOf("RemovePhysicalAckCorrelationMarker(existingGrid)",
            StringComparison.Ordinal);
        var binding = page.IndexOf("bubble.BindingContext is not ChatMessageItem",
            StringComparison.Ordinal);
        var read = page.IndexOf("PhysicalE2eAckCorrelationProvider.ReadCurrentAsync",
            StringComparison.Ordinal);
        var recheck = page.IndexOf("!ReferenceEquals(bubble.BindingContext, item)",
            StringComparison.Ordinal);
        Assert.True(clear >= 0 && clear < binding && binding < read && read < recheck,
            "A recycled bubble must be cleared before row lookup and rechecked after await.");
        Assert.Contains("catch (Exception)", page, StringComparison.Ordinal);
        Assert.Contains("Physical ACK correlation evidence is unavailable.", page,
            StringComparison.Ordinal);
    }

    private static string Read(string root, params string[] parts) =>
        File.ReadAllText(Path.Combine([root, .. parts]));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "Deep.Client.Maui.slnx")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("MAUI repository root was not found.");
    }
}
