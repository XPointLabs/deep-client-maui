namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class MauiAttachmentPickerServiceContractSmokeTests
{
    [Fact]
    public void GenericFileUsesSinglePickerAndEveryResultIsPreparedFailSafe()
    {
        var source = ReadServiceSource();

        Assert.Contains(
            "AttachmentPickKind.File => SingleOrEmpty(await FilePicker.Default.PickAsync(",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("FilePicker.Default.PickMultipleAsync", source, StringComparison.Ordinal);
        Assert.Contains("result is null ? [] : [result]", source, StringComparison.Ordinal);
        Assert.Contains("if (results.Count == 0)", source, StringComparison.Ordinal);
        Assert.Contains(
            "attachments.Add(await PrepareAsync(result, kind, cancellationToken).ConfigureAwait(false));",
            source,
            StringComparison.Ordinal);

        var pickerIndex = source.IndexOf(
            "IReadOnlyList<FileResult> results = kind switch",
            StringComparison.Ordinal);
        var cancellationBeforePickerIndex = source.LastIndexOf(
            "cancellationToken.ThrowIfCancellationRequested();",
            pickerIndex,
            StringComparison.Ordinal);
        var emptyResultIndex = source.IndexOf("if (results.Count == 0)", pickerIndex, StringComparison.Ordinal);
        var cancellationAfterPickerIndex = source.LastIndexOf(
            "cancellationToken.ThrowIfCancellationRequested();",
            emptyResultIndex,
            StringComparison.Ordinal);

        Assert.True(cancellationBeforePickerIndex >= 0 && cancellationBeforePickerIndex < pickerIndex);
        Assert.True(cancellationAfterPickerIndex > pickerIndex && cancellationAfterPickerIndex < emptyResultIndex);
    }

    [Fact]
    public void PhotoAndVideoKeepTheirDedicatedMediaPickerApis()
    {
        var source = ReadServiceSource();

        Assert.Contains(
            "AttachmentPickKind.Photo => await MediaPicker.Default.PickPhotosAsync(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "AttachmentPickKind.Video => await MediaPicker.Default.PickVideosAsync(",
            source,
            StringComparison.Ordinal);
    }

    private static string ReadServiceSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "Deep.Client.Maui.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(
            directory!.FullName,
            "src",
            "Deep.Client.Maui",
            "Services",
            "MauiAttachmentPickerService.cs"));
    }
}
