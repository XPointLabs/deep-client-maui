using System.Security.Cryptography;
using System.Text;
using Deep.Client.Maui.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class ActiveBuildNetworkIdTests
{
    [Fact]
    public void DevelopmentIdMatchesCanonicalDevopsDerivationAndProjectMetadata()
    {
        var derived = SHA256.HashData(
            Encoding.UTF8.GetBytes("deep-survival-dev-p10e-network-v1"));
        var expected = Convert.ToHexStringLower(derived.AsSpan(0, 16));

        Assert.Equal(ActiveBuildNetworkId.DevelopmentNetworkIdHex, expected);
        var project = File.ReadAllText(Path.Combine(
            WorkspaceRoot(), "src", "Deep.Client.Maui", "Deep.Client.Maui.csproj"));
        Assert.Contains(
            $">{ActiveBuildNetworkId.DevelopmentNetworkIdHex}</DeepDevelopmentNetworkId>",
            project,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExactLowerHexIsAccepted()
    {
        var value = ActiveBuildNetworkId.ParseExact(
            "0102030405060708090a0b0c0d0e0f10",
            "test");

        Assert.Equal(Enumerable.Range(1, 16).Select(static item => (byte)item), value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("0102030405060708090A0B0C0D0E0F10")]
    [InlineData("0102030405060708090a0b0c0d0e0f")]
    [InlineData("0102030405060708090a0b0c0d0e0f1000")]
    [InlineData("0102030405060708090a0b0c0d0e0g10")]
    public void NonCanonicalOrUnsafeValueIsRejected(string? value)
    {
        Assert.Throws<InvalidOperationException>(
            () => ActiveBuildNetworkId.ParseExact(value, "test"));
    }

    private static string WorkspaceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "Deep.Client.Maui.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Deep.Client.Maui workspace root was not found.");
    }
}
