using Deep.Client.Maui.Core.Services;

namespace Deep.Client.Maui.Core.ViewModels;

public sealed class OfflineUpdateVerificationViewModel : ViewModelBase
{
    private readonly IOfflineAndroidUpdateVerifier verifier;
    private bool isVerified;
    private bool isConfirmed;
    private string status = "Пакет ещё не проверен.";
    private string failure = string.Empty;
    private string source = string.Empty;
    private string packageId = string.Empty;
    private string version = string.Empty;
    private string versionCode = string.Empty;
    private string sourceCommit = string.Empty;
    private string expiry = string.Empty;

    public OfflineUpdateVerificationViewModel(IOfflineAndroidUpdateVerifier verifier)
    {
        this.verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
    }

    public bool IsVerified
    {
        get => isVerified;
        private set
        {
            if (SetProperty(ref isVerified, value))
            {
                RaisePropertyChanged(nameof(CanConfirm));
            }
        }
    }

    public bool IsConfirmed
    {
        get => isConfirmed;
        private set => SetProperty(ref isConfirmed, value);
    }

    public bool CanConfirm => IsVerified && !IsConfirmed;

    public string Status
    {
        get => status;
        private set => SetProperty(ref status, value);
    }

    public string Failure
    {
        get => failure;
        private set => SetProperty(ref failure, value);
    }

    public string Source
    {
        get => source;
        private set => SetProperty(ref source, value);
    }

    public string Version
    {
        get => version;
        private set => SetProperty(ref version, value);
    }

    public string PackageId
    {
        get => packageId;
        private set => SetProperty(ref packageId, value);
    }

    public string VersionCode
    {
        get => versionCode;
        private set => SetProperty(ref versionCode, value);
    }

    public string SourceCommit
    {
        get => sourceCommit;
        private set => SetProperty(ref sourceCommit, value);
    }

    public string Expiry
    {
        get => expiry;
        private set => SetProperty(ref expiry, value);
    }

    public async Task VerifyAsync(
        OfflineAndroidPackageRequest request,
        CancellationToken cancellationToken = default)
    {
        IsConfirmed = false;
        var result = await verifier.VerifyAsync(request, cancellationToken);
        IsVerified = result.IsVerified;
        Status = result.Status;
        Failure = result.Failure ?? string.Empty;
        Source = result.SourceLabel;
        PackageId = result.PackageId;
        Version = result.VersionName;
        VersionCode = result.VersionCode;
        SourceCommit = result.SourceCommit;
        Expiry = result.MetadataExpiresAtUtc == DateTimeOffset.MinValue
            ? string.Empty
            : result.MetadataExpiresAtUtc.ToString("yyyy-MM-dd HH:mm 'UTC'");
    }

    public bool ConfirmExactDetails(
        string typedPackageId,
        string typedVersion,
        string typedSourceCommit,
        string typedExpiry)
    {
        if (!CanConfirm ||
            !string.Equals(typedPackageId, PackageId, StringComparison.Ordinal) ||
            !string.Equals(typedVersion, Version, StringComparison.Ordinal) ||
            !string.Equals(typedSourceCommit, SourceCommit, StringComparison.Ordinal) ||
            !string.Equals(typedExpiry, Expiry, StringComparison.Ordinal))
        {
            return false;
        }

        IsConfirmed = true;
        Status = "Версия подтверждена вручную. Можно передать проверенный снимок системному установщику.";
        RaisePropertyChanged(nameof(CanConfirm));
        return true;
    }
}
