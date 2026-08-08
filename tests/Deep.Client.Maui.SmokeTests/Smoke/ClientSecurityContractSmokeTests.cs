namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class ClientSecurityContractSmokeTests
{
    [Fact]
    public void ProductionMailboxIdentityIsMeasuredFromTheRunningSignedArtifact()
    {
        var attestor = ReadWorkspaceFile(
            "src", "Deep.Client.Maui", "Services",
            "ProductionMailboxClientIdentityAttestor.cs");
        var artifactDigest = ReadWorkspaceFile(
            "src", "Deep.Client.Maui", "Services",
            "ProductionMailboxArtifactSetDigest.cs");
        var transparency = ReadWorkspaceFile(
            "src", "Deep.Client.Maui", "Services",
            "ProductionAndroidCodeTransparency.cs");
        Assert.Contains("context.PackageName", attestor, StringComparison.Ordinal);
        Assert.Contains("GetApkContentsSigners", attestor, StringComparison.Ordinal);
        Assert.Contains("application?.SourceDir", attestor, StringComparison.Ordinal);
        Assert.Contains("application?.SplitSourceDirs", attestor, StringComparison.Ordinal);
        Assert.Contains("GetSigningCertificateHistory", attestor, StringComparison.Ordinal);
        Assert.Contains("Environment.ProcessPath", attestor, StringComparison.Ordinal);
        Assert.Contains("AppxSignature.p7x", attestor, StringComparison.Ordinal);
        Assert.Contains("new SignedCms()", attestor, StringComparison.Ordinal);
        Assert.Contains("signed.CheckSignature", attestor, StringComparison.Ordinal);
        Assert.Contains("ProductionMailboxArtifactSetDigest.ComputeAsync", attestor,
            StringComparison.Ordinal);
        Assert.Contains("ProductionAndroidCodeTransparencyVerifier.VerifyRuntimeAsync", attestor,
            StringComparison.Ordinal);
        Assert.Contains("PublicKeyAuth.VerifyDetached", transparency, StringComparison.Ordinal);
        Assert.Contains("ProductionAndroidSemanticApkDigest.ComputeAsync", transparency,
            StringComparison.Ordinal);
        Assert.Contains("TryLoadAndroidIdentity", attestor, StringComparison.Ordinal);
        Assert.Contains("beforeTransparency", attestor, StringComparison.Ordinal);
        Assert.DoesNotContain("buildIdentity.BuildIdSha256", attestor,
            StringComparison.Ordinal);
        Assert.Contains("CaptureAndroidPackage", attestor, StringComparison.Ordinal);
        Assert.Contains("Production Android package changed during attestation.", attestor,
            StringComparison.Ordinal);
        Assert.Contains("CaptureWindowsInventory", attestor, StringComparison.Ordinal);
        Assert.Contains("SHA256.HashDataAsync", artifactDigest, StringComparison.Ordinal);
        Assert.Contains("EnsureNoReparsePoints", artifactDigest, StringComparison.Ordinal);
        Assert.DoesNotContain("GetEnvironmentVariable", attestor, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionMailboxTrustFloorIsCompiledAndNeverReadFromRuntimeEnvironment()
    {
        var project = ReadWorkspaceFile(
            "src", "Deep.Client.Maui", "Deep.Client.Maui.csproj");
        var trustFloor = ReadWorkspaceFile(
            "src", "Deep.Client.Maui", "Services",
            "ProductionMailboxBuildTrustFloor.cs");
        Assert.Contains("AssemblyMetadata Include=\"DeepProductionMrXPublicKeySha256\"",
            project, StringComparison.Ordinal);
        Assert.Contains("ValidateProductionMailboxTrustFloor", project,
            StringComparison.Ordinal);
        Assert.Contains("RequireProductionMailboxTrustFloor", project,
            StringComparison.Ordinal);
        Assert.Contains("RequireProductionAndroidBuildIdentity", project,
            StringComparison.Ordinal);
        Assert.Contains("DeepProductionAndroidSignerLineageSha256", project,
            StringComparison.Ordinal);
        Assert.Contains("DeepProductionAndroidCodeTransparencyManifest", project,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AssemblyMetadata Include=\"DeepProductionAndroidBuildIdSha256\"",
            project, StringComparison.Ordinal);
        Assert.Contains("RequireProductionAndroidCodeTransparency", project,
            StringComparison.Ordinal);
        Assert.Contains("Release Android and Windows builds require the complete production mailbox trust floor.",
            project, StringComparison.Ordinal);
        Assert.Contains("'$(Configuration)' != 'Release'", project,
            StringComparison.Ordinal);
        Assert.Contains("GetCustomAttributes<AssemblyMetadataAttribute>()", trustFloor,
            StringComparison.Ordinal);
        Assert.DoesNotContain("GetEnvironmentVariable", trustFloor,
            StringComparison.Ordinal);
        foreach (var scriptName in new[]
        {
            "build-android-play.ps1",
            "build-windows-msix.ps1"
        })
        {
            var script = ReadWorkspaceFile("eng", scriptName);
            Assert.Contains("TrustFloorBundle", script, StringComparison.Ordinal);
            Assert.Contains("Import-ProductionTrustBundle", script,
                StringComparison.Ordinal);
            Assert.Contains("trustMsBuildArguments", script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RecoveryPhraseEditorDisablesLearningPredictionSpellcheckAndAutofill()
    {
        var xaml = ReadWorkspaceFile("src", "Deep.Client.Maui", "Pages", "OnboardingPage.xaml");
        var codeBehind = ReadWorkspaceFile("src", "Deep.Client.Maui", "Pages", "OnboardingPage.xaml.cs");

        Assert.Contains("IsSpellCheckEnabled=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsTextPredictionEnabled=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Keyboard=\"Plain\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ImportantForAutofill.NoExcludeDescendants", codeBehind, StringComparison.Ordinal);
        Assert.Contains("TextFlagNoSuggestions", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ImeFlags.NoPersonalizedLearning", codeBehind, StringComparison.Ordinal);
        Assert.Contains("UITextAutocorrectionType.No", codeBehind, StringComparison.Ordinal);
        Assert.Contains("UITextInlinePredictionType.No", codeBehind, StringComparison.Ordinal);
        Assert.Contains("UIWritingToolsBehavior.None", codeBehind, StringComparison.Ordinal);
        Assert.Contains("new Foundation.NSString(string.Empty)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("editor.IsTextPredictionEnabled = false", codeBehind, StringComparison.Ordinal);
        Assert.Contains("protected override void OnDisappearing()", codeBehind, StringComparison.Ordinal);
        Assert.Contains("viewModel.ClearRecoveryPhrase();", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Placeholder=\"Фраза восстановления (12 или 13 слов)\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidPlayBuildPinsUploadCertificateForApkAndAab()
    {
        var script = ReadWorkspaceFile("eng", "build-android-play.ps1");

        Assert.Contains(
            "BF8ABED56E852D0902796F9E0131789F188688784A07AD516760F127684204C2",
            script,
            StringComparison.Ordinal);
        Assert.Contains("--print-certs $candidateApk", script, StringComparison.Ordinal);
        Assert.Contains("Assert-UploadCertificateSha256 -ArtifactName \"APK\"", script, StringComparison.Ordinal);
        Assert.Contains("-printcert -jarfile $bundle.FullName -rfc", script, StringComparison.Ordinal);
        Assert.Contains("Assert-UploadCertificateSha256 -ArtifactName \"AAB\"", script, StringComparison.Ordinal);
        Assert.Contains("build-apks", script, StringComparison.Ordinal);
        Assert.Contains("--mode=default", script, StringComparison.Ordinal);
        Assert.Contains("Deep.AndroidTransparency.Tool", script, StringComparison.Ordinal);
        Assert.True(script.IndexOf("Generated default APK set does not match", StringComparison.Ordinal) <
            script.IndexOf("Copy-Item -LiteralPath $bundle.FullName -Destination $finalBundle", StringComparison.Ordinal));
        Assert.DoesNotContain("jarsigner -strict", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AndroidTransparencyPreparationIsNonPublishingAndHasNoMrXSigner()
    {
        var preparation = ReadWorkspaceFile(
            "eng", "prepare-android-code-transparency.ps1");
        var publish = ReadWorkspaceFile("eng", "build-android-play.ps1");
        var tool = ReadWorkspaceFile(
            "eng", "tools", "Deep.AndroidTransparency.Tool", "Program.cs");
        var project = ReadWorkspaceFile(
            "src", "Deep.Client.Maui", "Deep.Client.Maui.csproj");

        Assert.Contains("DeepProductionAndroidTransparencyPreparation=true",
            preparation, StringComparison.Ordinal);
        Assert.Contains("build-apks", preparation, StringComparison.Ordinal);
        Assert.Contains("--mode=default", preparation, StringComparison.Ordinal);
        Assert.Contains("$archive.Entries.Count -gt 16384", preparation,
            StringComparison.Ordinal);
        Assert.Contains("$written -ne $entry.Length", preparation,
            StringComparison.Ordinal);
        Assert.DoesNotContain("$source.CopyTo($target)", preparation,
            StringComparison.Ordinal);
        Assert.Contains("-- prepare", preparation, StringComparison.Ordinal);
        Assert.DoesNotContain("artifacts\\android-release", preparation,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Copy-Item -LiteralPath $bundle", preparation,
            StringComparison.Ordinal);
        Assert.DoesNotContain("DeepProductionAndroidTransparencyPreparation=true", publish,
            StringComparison.Ordinal);
        Assert.Contains("DeepGoogleServicesJson", preparation, StringComparison.Ordinal);
        Assert.Contains("DeepGoogleServicesJson", publish, StringComparison.Ordinal);
        Assert.Contains("Open-PinnedAndroidBundletool", preparation, StringComparison.Ordinal);
        Assert.Contains("Open-PinnedAndroidBundletool", publish, StringComparison.Ordinal);
        Assert.Contains("Close-PinnedAndroidBundletool", preparation, StringComparison.Ordinal);
        Assert.Contains("Close-PinnedAndroidBundletool", publish, StringComparison.Ordinal);
        Assert.DoesNotContain("firebaseTarget", preparation, StringComparison.Ordinal);
        Assert.DoesNotContain("firebaseTarget", publish, StringComparison.Ordinal);
        Assert.DoesNotContain("Copy-Item -LiteralPath $GoogleServicesJson", preparation,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Copy-Item -LiteralPath $GoogleServicesJson", publish,
            StringComparison.Ordinal);
        Assert.DoesNotContain("SignDetached", tool, StringComparison.Ordinal);
        Assert.DoesNotContain("PrivateKey", tool, StringComparison.Ordinal);
        Assert.Contains("EnsureNoReparse(parent)", tool, StringComparison.Ordinal);
        var bundletoolGate = ReadWorkspaceFile("eng", "Assert-AndroidBundletool.ps1");
        Assert.Contains(
            "a099cfa1543f55593bc2ed16a70a7c67fe54b1747bb7301f37fdfd6d91028e29",
            bundletoolGate, StringComparison.Ordinal);
        Assert.Contains("FileShare]::None", bundletoolGate, StringComparison.Ordinal);
        Assert.Contains("FileShare]::Read", bundletoolGate, StringComparison.Ordinal);
        Assert.Contains("New-PrivateBundletoolDirectory", bundletoolGate,
            StringComparison.Ordinal);
        Assert.Contains("'$(DeepProductionAndroidTransparencyPreparation)' != 'true'",
            project, StringComparison.Ordinal);
        Assert.Contains("GoogleServicesJson Include=\"$(DeepGoogleServicesJson)\"",
            project, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionFileTransportPinsBothDirectOriginAndCloudflareFallback()
    {
        var program = ReadWorkspaceFile("src", "Deep.Client.Maui", "MauiProgram.cs");
        var releaseEnvironment = ReadWorkspaceFile("src", "Deep.Client.Maui", "deep.release.env");

        Assert.Contains("DEEP_FILE_CONNECT_IPS=111.235.151.150", releaseEnvironment, StringComparison.Ordinal);
        Assert.Contains("DEEP_FILE_TLS_PUBLIC_KEY_PINS=", releaseEnvironment, StringComparison.Ordinal);
        Assert.Contains("sha256/wgviOsKm6Q2dzxS5lvwSsa+/b3wvm6lyRdOg9zj5CUs=", releaseEnvironment, StringComparison.Ordinal);
        Assert.Contains("sha256/nM7gwVgoneQys6mWu2C/Bo3RGY6NSshlPBbkqlZVzOE=", releaseEnvironment, StringComparison.Ordinal);
        Assert.Contains(
            "CreateCertificatePinningValidationCallback(FileTlsPublicKeyPinsEnv)",
            program,
            StringComparison.Ordinal);
        Assert.Contains("policyErrors != System.Net.Security.SslPolicyErrors.None", program, StringComparison.Ordinal);
        Assert.DoesNotContain("allowPinnedChainErrors", program, StringComparison.Ordinal);
        Assert.Contains("connectCallback = (context, cancellationToken) =>", program, StringComparison.Ordinal);
        Assert.Contains("address.AddressFamily", program, StringComparison.Ordinal);
        Assert.Contains("FileConnectFallbackDelay", program, StringComparison.Ordinal);
        Assert.Contains("FileConnectAttemptTimeout", program, StringComparison.Ordinal);
        Assert.Contains("Task.WhenAny(pending)", program, StringComparison.Ordinal);
        Assert.Contains("cancellationToken.ThrowIfCancellationRequested();", program, StringComparison.Ordinal);
    }

    [Fact]
    public void AvatarSelectionIsDecodedOrientedReencodedBoundedAndMetadataFree()
    {
        var source = ReadWorkspaceFile("src", "Deep.Client.Maui", "Services", "ProfileAvatarSync.cs");

        Assert.Contains("MaximumInputBytes", source, StringComparison.Ordinal);
        Assert.Contains("MaximumInputPixels", source, StringComparison.Ordinal);
        Assert.Contains("MaximumAvatarDimension", source, StringComparison.Ordinal);
        Assert.Contains("MaximumAvatarBytes", source, StringComparison.Ordinal);
        Assert.Contains("NormalizeToJpegAsync", source, StringComparison.Ordinal);
        Assert.Contains("bounds.OutMimeType", source, StringComparison.Ordinal);
        Assert.Contains("source.TypeIdentifier", source, StringComparison.Ordinal);
        Assert.Contains("decoder.DecoderInformation.CodecId", source, StringComparison.Ordinal);
        Assert.Contains("AndroidExifOrientationNormalizer.ApplyIfNeeded", source, StringComparison.Ordinal);
        Assert.Contains("CreateThumbnailWithTransform = true", source, StringComparison.Ordinal);
        Assert.Contains("ExifOrientationMode.RespectExifOrientation", source, StringComparison.Ordinal);
        Assert.Contains("ValidateNormalizedJpeg", source, StringComparison.Ordinal);
        Assert.Contains("marker is 0xE1 or 0xED or 0xFE", source, StringComparison.Ordinal);
        Assert.Contains("File.Move(temporaryPath, avatarPath, overwrite: true)", source, StringComparison.Ordinal);
        Assert.Contains("Array.Clear(original)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("source.CopyToAsync(destination", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveContentType(photo)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AttachmentPickerBoundsProviderStreamsBeforeWritingTheCacheFile()
    {
        var source = ReadWorkspaceFile(
            "src",
            "Deep.Client.Maui",
            "Services",
            "MauiAttachmentPickerService.cs");

        Assert.Contains("CopyInputWithLimitAsync", source, StringComparison.Ordinal);
        Assert.Contains("source.CanSeek && source.Length > MaxAttachmentBytes", source, StringComparison.Ordinal);
        Assert.Contains("copied > MaxAttachmentBytes", source, StringComparison.Ordinal);
        Assert.DoesNotContain("stream.CopyToAsync(output", source, StringComparison.Ordinal);
    }

    [Fact]
    public void UiAutomationStubIsDebugOnlyAndReleaseStillRejectsCleartextAndImplicitStubs()
    {
        var program = ReadWorkspaceFile("src", "Deep.Client.Maui", "MauiProgram.cs");

        Assert.Contains("internal const string E2eBootstrapEnv", program, StringComparison.Ordinal);
        var runtimeFactoryIndex = program.IndexOf(
            "private static async Task<ClientRuntime> CreateClientRuntimeAsync(",
            StringComparison.Ordinal);
        Assert.True(runtimeFactoryIndex >= 0);
        var bootstrapIndex = program.IndexOf(
            "Environment.GetEnvironmentVariable(E2eBootstrapEnv)",
            runtimeFactoryIndex,
            StringComparison.Ordinal);
        Assert.True(bootstrapIndex >= 0);
        var debugGuardIndex = program.LastIndexOf("#if DEBUG", bootstrapIndex, StringComparison.Ordinal);
        var debugGuardEndIndex = program.IndexOf("#endif", bootstrapIndex, StringComparison.Ordinal);
        var stubRuntimeIndex = program.IndexOf(
            "return ClientRuntime.CreateStubbed(",
            bootstrapIndex,
            StringComparison.Ordinal);
        Assert.True(debugGuardIndex >= 0);
        Assert.True(debugGuardEndIndex > bootstrapIndex);
        Assert.InRange(stubRuntimeIndex, bootstrapIndex + 1, debugGuardEndIndex - 1);
        Assert.Contains(
            "#if !DEBUG\r\n        if (pins.Count == 0)",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "must use HTTPS in non-Debug builds.",
            program,
            StringComparison.Ordinal);
        Assert.Contains("IsExplicitLoopbackHttp(uri)", program, StringComparison.Ordinal);
    }

    [Fact]
    public void PersistentRuntimeFactoryUsesTheExecutableCompositionCoveredByRuntimeTests()
    {
        var program = ReadWorkspaceFile("src", "Deep.Client.Maui", "MauiProgram.cs");

        Assert.Contains(
            "return PersistentClientRuntimeComposer.Create(",
            program,
            StringComparison.Ordinal);
        var composer = ReadWorkspaceFile(
            "src", "Deep.Client.Maui", "Services",
            "PersistentClientRuntimeComposer.cs");
        Assert.Contains("membershipRouteCatalogProvider.Bind(", composer, StringComparison.Ordinal);
        Assert.Contains(
            "secureStore = new SecureRecoverySessionStore(",
            composer,
            StringComparison.Ordinal);
        Assert.DoesNotContain("client-state.json", program, StringComparison.Ordinal);
        Assert.DoesNotContain("legacyStatePath", program, StringComparison.Ordinal);
        Assert.DoesNotContain("FileSystemLegacyStateArtifacts", program, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureEncryptedDatabase", program, StringComparison.Ordinal);
        Assert.DoesNotContain("encrypted-migration", program, StringComparison.Ordinal);
        Assert.DoesNotContain("plaintext-migration", program, StringComparison.Ordinal);
        Assert.Contains(
            "PrelaunchPlaintextStateArtifactPurger",
            program,
            StringComparison.Ordinal);
        Assert.DoesNotContain("legacyStatePath", composer, StringComparison.Ordinal);
        Assert.DoesNotContain("legacyInMemoryStatePath", composer, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupOffersDestructiveResetOnlyForTypedLocalStateFailures()
    {
        var app = ReadWorkspaceFile("src", "Deep.Client.Maui", "App.xaml.cs");
        var policy = ReadWorkspaceFile(
            "src", "Deep.Client.Maui", "Services", "StartupLocalStateReset.cs");

        Assert.Contains("catch (LocalStateResetRequiredException", app, StringComparison.Ordinal);
        Assert.Contains("StartupResetLocalStateButton", app, StringComparison.Ordinal);
        Assert.Contains("AutomationId = \"Startup.Status\"", app, StringComparison.Ordinal);
        Assert.Contains("AutomationId = \"Startup.Error\"", app, StringComparison.Ordinal);
        Assert.Contains("AutomationId = \"Startup.RuntimeFailureCode\"", app,
            StringComparison.Ordinal);
        Assert.Contains("AutomationId = \"Startup.Retry\"", app, StringComparison.Ordinal);
        Assert.Contains("DisplayAlertAsync(", app, StringComparison.Ordinal);
        Assert.Contains("Сбросить локальные данные", app, StringComparison.Ordinal);
        Assert.Contains("localStateResetContext.TryRequestConfirmedReset()", app, StringComparison.Ordinal);
        Assert.Contains("exception.GetType() == typeof(LocalStateResetRequiredException)", policy, StringComparison.Ordinal);
        Assert.Contains("Preferences.Default.Set(WipeLocalDataOnNextLaunchKey, true)", policy, StringComparison.Ordinal);
    }

    [Fact]
    public void PhysicalE2eMembershipCleartextIsBuildAndRuntimeGated()
    {
        var program = ReadWorkspaceFile(
            "src", "Deep.Client.Maui", "MauiProgram.cs");
        var project = ReadWorkspaceFile(
            "src", "Deep.Client.Maui", "Deep.Client.Maui.csproj");
        var productionPolicy = ReadWorkspaceFile(
            "src", "Deep.Client.Maui", "Platforms", "Android", "Resources",
            "xml", "network_security_config.xml");
        var physicalPolicy = ReadWorkspaceFile(
            "src", "Deep.Client.Maui", "Platforms", "Android", "Resources",
            "xml", "network_security_config_physical_e2e.xml");
        var composition = ReadWorkspaceFile(
            "src", "Deep.Client.Maui.Core", "Services",
            "DevLocalMembershipRouteComposition.cs");
        var httpComposition = ReadWorkspaceFile(
            "src", "Deep.Client.Maui.Core", "Services",
            "ApplicationHttpTransportComposition.cs");

        Assert.Contains(
            "'$(DeepPhysicalE2E)' == 'true' And '$(Configuration)' != 'Release'",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "<AndroidResource Remove=\"Platforms\\Android\\Resources\\xml\\network_security_config.xml\"",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "<AndroidResource Remove=\"Platforms\\Android\\Resources\\xml\\network_security_config_physical_e2e.xml\"",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "<base-config cleartextTrafficPermitted=\"false\"",
            productionPolicy,
            StringComparison.Ordinal);
        Assert.Contains(
            "<base-config cleartextTrafficPermitted=\"true\"",
            physicalPolicy,
            StringComparison.Ordinal);
        Assert.Contains("productionBuild", composition, StringComparison.Ordinal);
        Assert.Contains("explicitDevelopmentProfile", composition, StringComparison.Ordinal);
        Assert.Contains("MembershipRouteEndpointPolicy.DevLocalHttp", composition, StringComparison.Ordinal);
        Assert.Contains("new SodiumEd25519MembershipSignatureVerifier()", composition, StringComparison.Ordinal);
        Assert.Contains(
            "survivalDevelopment && IsDebugBuild()",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "RoutedRuntimeEndpointPolicy.PhysicalE2eDevelopment",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "HttpServiceEndpointPolicy.PhysicalE2eDevelopment",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "httpTransportFactories.ServiceTransportFactory",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "ApplicationHttpTransportComposition.CreateBoundNetwork(",
            program,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            httpComposition.Split(
                "transportFactory.BindNetwork(",
                StringSplitOptions.None).Length - 1);
        Assert.Contains(
            "CreateServiceTransportNetworkHooks()",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "CreateFileTransportNetworkHooks(fileConnectIps)",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "RoutedRuntimeConfiguration.ValidateAtLeastThree(",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "inputs.RoutedEndpointPolicy",
            program,
            StringComparison.Ordinal);
    }

    private static string ReadWorkspaceFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Deep.Client.Maui.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine([directory!.FullName, .. parts]));
    }
}
