namespace Deep.Client.Maui.Clean.Tests;

public sealed class CleanStartupCompositionTests
{
    [Fact]
    public async Task AndroidKeyboardUsesActualServiceStateAndRejectsAmbiguousInput()
    {
        // Parsed production function with synthetic dumps only, not physical evidence.
        var script = Path.Combine(FindRepository(), "eng", "Invoke-PhysicalDid2AccountProbeAndroid.ps1");
        var source = File.ReadAllText(script);
        Assert.Contains("@('shell', 'dumpsys', 'input_method')", source);
        Assert.Contains("$imeShowing = Get-ProbeImeShowing $inputMethod", source);
        Assert.Contains("ImeShowing=$imeShowing", source);
        Assert.Contains("keyboardShown=$imeShowing", source);
        Assert.DoesNotContain("($focus -match 'mImeShowing=true')", source);
        Assert.True(source.IndexOf("throw 'The exact DID2 package does not own the foreground window.'", StringComparison.Ordinal) <
            source.IndexOf("$inputMethod = Invoke-Adb", StringComparison.Ordinal));
        var start = new System.Diagnostics.ProcessStartInfo("pwsh")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["DEEP_DIAGNOSTIC_SCRIPT"] = script;
        const string test = """
            $ErrorActionPreference='Stop'
            $tokens=$null;$errors=$null
            $ast=[Management.Automation.Language.Parser]::ParseFile($env:DEEP_DIAGNOSTIC_SCRIPT,[ref]$tokens,[ref]$errors)
            if ($errors.Count) { throw 'Parser failed' }
            $functions=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Get-ProbeImeShowing'},$true))
            if ($functions.Count -ne 1) { throw 'Function scope failed' }
            . ([scriptblock]::Create($functions[0].Extent.Text))
            $prefix='  mShowRequested=false mShowExplicitlyRequested=false mShowForced=false mInputShown='
            foreach($ending in @("`n", "`r`n")) {
                if (Get-ProbeImeShowing ("mImeShowing=true`n"+$prefix+'false'+$ending)) { throw 'Stale window flag authorized keyboard dismissal' }
                if (-not (Get-ProbeImeShowing ("mImeShowing=false`n"+$prefix+'true'+$ending))) { throw 'Actual shown keyboard was ignored' }
            }
            foreach($invalid in @('', 'mImeShowing=true', 'mInputShown=false',
                ($prefix+'unknown'), ($prefix+'false extra'), ($prefix+'false'+"`n"+$prefix+'true'),
                ($prefix+'false'+"`nprivate mInputShown=false"),
                ('payload '+$prefix+'false'), ('x'*1048577))) {
                $seen=$false
                try { $null=Get-ProbeImeShowing $invalid } catch {
                    $seen=$true
                    if ($_.Exception.Message -cne 'DID2 input-method state is unavailable.') { throw 'Diagnostic leaked unapproved input' }
                }
                if (-not $seen) { throw 'Unknown or ambiguous service state accepted' }
            }
            Write-Output 'passed'
            """;
        foreach (var argument in new[] { "-NoProfile", "-Command", test }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        Assert.Equal(string.Empty, (await errors).Trim());
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("passed", (await output).Trim());
    }

    [Fact]
    public void DiagnosticNetworkLifecycleUsesVisibilityAndExposesOnlyClosedReconnectState()
    {
        var app = ReadSource("App.Did2.cs");
        Assert.Contains("window.Stopped += (_,_) => reconnect?.Suspend();", app);
        Assert.Contains("window.Resumed += (_,_) => reconnect?.Resume();", app);
        Assert.Contains("window.Destroying += (_,_) => reconnect?.Suspend();", app);
        Assert.DoesNotContain("window.Deactivated +=", app);
        var shell = ReadSource("AppShell.Did2.cs");
        Assert.Contains("Status(\"Did2Probe.ReconnectState\")", shell);
        Assert.Contains("nameof(account.NetworkState)", shell);
        var driver = File.ReadAllText(Path.Combine(FindRepository(), "eng", "Invoke-PhysicalDid2AccountProbeAndroid.ps1"));
        Assert.Contains("@('Dormant', 'Offline', 'Reconnecting', 'Verified', 'Unavailable', 'Rejected')", driver);
        Assert.Contains("reconnectState=$reconnectState", driver);
    }

    [Fact]
    public void ManualNetworkAndContactReverificationUseTheSameReconnectPipeline()
    {
        var source = File.ReadAllText(Path.Combine(FindRepository(), "src", "Deep.Client.Maui.Core",
            "ViewModels", "DeepIdV2AccountViewModel.cs"));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(source,
            System.Text.RegularExpressions.Regex.Escape("else await reconnect.VerifyNowAsync(ct);")).Count);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(source,
            System.Text.RegularExpressions.Regex.Escape("if (reconnect is null) await networkAdmission.VerifyAsync(accounts, ct);")).Count);
    }

    [Fact]
    public async Task AndroidMessagingEditorRejectsForeignFocusAndExistingDraftBeforeTyping()
    {
        // Pure parsed-function test with synthetic XML and an ADB stub, not UI evidence.
        var script = Path.Combine(FindRepository(), "eng", "Invoke-PhysicalDid2AccountProbeAndroid.ps1");
        var start = new System.Diagnostics.ProcessStartInfo("pwsh")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["DEEP_DIAGNOSTIC_SCRIPT"] = script;
        const string test = """
            $ErrorActionPreference='Stop'
            $tokens=$null;$errors=$null
            $ast=[Management.Automation.Language.Parser]::ParseFile($env:DEEP_DIAGNOSTIC_SCRIPT,[ref]$tokens,[ref]$errors)
            if ($errors.Count) { throw 'Parser failed' }
            foreach ($name in @('Click-ProbeNode','Set-ProbeEmptyEditor')) {
                $matches=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name},$true))
                if ($matches.Count -ne 1) { throw 'Function scope failed' }
                . ([scriptblock]::Create($matches[0].Extent.Text))
            }
            $probePackage='network.xpoint.deep.did2https'
            $script:commands=[Collections.Generic.List[object]]::new()
            function Invoke-Adb([string[]]$Arguments,[string]$Label) { $script:commands.Add($Arguments) }
            function Node([string]$Package,[string]$Id,[string]$Text,[string]$Focused,[string]$Bounds='[10,20][110,120]',[string]$Enabled='true') {
                $doc=[Xml.XmlDocument]::new()
                $doc.LoadXml('<node/>')
                foreach($pair in @(@('package',$Package),@('resource-id',"${probePackage}:id/$Id"),@('text',$Text),
                    @('focused',$Focused),@('bounds',$Bounds),@('enabled',$Enabled),@('class','android.widget.EditText'))) {
                    $doc.DocumentElement.SetAttribute($pair[0],$pair[1])
                }
                return $doc.DocumentElement
            }
            function Read-ProbeUi { return @{Nodes=@($script:focus)} }
            foreach($id in @('Contacts.Address','Message.Text')) {
                $hint=if($id -ceq 'Contacts.Address'){'Deep ID контакта'}else{'Сообщение'}
                foreach($empty in @('', $hint)) {
                    $script:commands.Clear()
                    $field=Node $probePackage $id $empty 'false'
                    $script:focus=Node $probePackage $id '' 'true'
                    Set-ProbeEmptyEditor @{Nodes=@($field)} $id 'DID2 Android QA 20261015 A'
                    if($script:commands.Count -ne 2 -or $script:commands[0][2] -cne 'tap' -or
                        $script:commands[1][2] -cne 'text' -or $script:commands[1][3] -cne 'DID2%sAndroid%sQA%s20261015%sA') {
                        throw 'Exact focused synthetic input failed'
                    }
                }
                $script:commands.Clear();$seen=$false
                try { Set-ProbeEmptyEditor @{Nodes=@((Node $probePackage $id 'existing draft' 'false'))} $id 'test' }
                catch { $seen=$true }
                if(-not $seen -or $script:commands.Count -ne 0) { throw 'Existing draft changed' }
                $script:commands.Clear();$seen=$false
                $script:focus=Node 'foreign.package' $id '' 'true'
                try { Set-ProbeEmptyEditor @{Nodes=@((Node $probePackage $id '' 'false'))} $id 'test' }
                catch { $seen=$true }
                if(-not $seen -or $script:commands.Count -ne 1) { throw 'Foreign focused field received input' }
            }
            foreach($invalid in @(
                (Node 'foreign.package' 'Message.Text' '' 'false'),
                (Node $probePackage 'Message.Text' '' 'false' '[0,0][0,0]'),
                (Node $probePackage 'Message.Text' '' 'false' '[0,0][110,120]' 'false'))) {
                $script:commands.Clear();$seen=$false
                try { Click-ProbeNode $invalid } catch { $seen=$true }
                if(-not $seen -or $script:commands.Count -ne 0) { throw 'Invalid owned-node boundary was crossed' }
            }
            Write-Output 'passed'
            """;
        foreach (var argument in new[] { "-NoProfile", "-Command", test }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(deadline.Token);
        Assert.True(process.ExitCode == 0, await errors);
        Assert.Contains("passed", await output);
    }

    [Fact]
    public void AndroidMessagingPhasesRetainPackageProvenanceAndDoNotRecordPeerOrPayload()
    {
        var script = File.ReadAllText(Path.Combine(FindRepository(), "eng", "Invoke-PhysicalDid2AccountProbeAndroid.ps1"));
        Assert.Contains("$Phase -in $messagingPhases -and $Lane -cne 'Did2Https'", script);
        Assert.Contains("$PSBoundParameters.ContainsKey('PeerAddress') -ne ($Phase -ceq 'SetPeerAddress')", script);
        Assert.Contains("[ValidateSet('DID2 Android QA 20261015 A')]", script);
        Assert.Contains("Exactly one observed conversation row is required", script);
        Assert.Contains("$lists[0].SelectNodes('.//node')", script);
        Assert.Contains("Click-ProbeControl $ui 'Message.Send'", script);
        Assert.Contains("$_.GetAttribute('focused') -ceq 'true'", script);
        Assert.Contains("Assert-SameSnapshot $before[$package] $after[$package] $package", script);
        Assert.Contains("merge-base --is-ancestor $apkSourceCommit $commit", script);
        Assert.DoesNotContain("peerAddress =", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("qaText =", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("deviceDeliveryVerified = $false", script);
    }

    [Fact]
    public async Task AndroidTransitionReadbackRetriesOnlyOneObservationNeverTheAction()
    {
        var script = Path.Combine(FindRepository(), "eng", "Invoke-PhysicalDid2AccountProbeAndroid.ps1");
        var start = new System.Diagnostics.ProcessStartInfo("pwsh")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["DEEP_DIAGNOSTIC_SCRIPT"] = script;
        const string test = """
            $ErrorActionPreference='Stop'
            $tokens=$null;$errors=$null
            $ast=[Management.Automation.Language.Parser]::ParseFile($env:DEEP_DIAGNOSTIC_SCRIPT,[ref]$tokens,[ref]$errors)
            if ($errors.Count -ne 0) { throw 'Parser failed' }
            $functions=@($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Read-ProbeUiAfterAction' },$true))
            if ($functions.Count -ne 1) { throw 'Function scope failed' }
            . ([scriptblock]::Create($functions[0].Extent.Text))
            function Read-ProbeUi {
                $script:calls++
                if ($script:calls -le $script:failures) { throw $script:message }
                return @{Summary=@{status='observed'}}
            }
            foreach ($message in @('DID2 UI hierarchy is unavailable.','The owned reset window lacks the exact diagnostic confirmation UI.')) {
                $script:message=$message;$script:calls=0;$script:failures=1
                $observed=Read-ProbeUiAfterAction
                if ($script:calls -ne 2 -or $observed.Summary.status -cne 'observed') { throw 'Bounded reobservation failed' }
                $script:calls=0;$script:failures=2;$errorSeen=$false
                try { Read-ProbeUiAfterAction } catch { $errorSeen=$true }
                if (-not $errorSeen -or $script:calls -ne 2) { throw 'Repeated transition must fail closed' }
            }
            $script:calls=0;$script:failures=0
            $null=Read-ProbeUiAfterAction
            if ($script:calls -ne 1) { throw 'Stable readback must not repeat' }
            foreach ($message in @('The exact DID2 package does not own the foreground window.','Protected package changed.')) {
                $script:message=$message;$script:calls=0;$script:failures=1;$errorSeen=$false
                try { Read-ProbeUiAfterAction } catch { $errorSeen=$true }
                if (-not $errorSeen -or $script:calls -ne 1) { throw 'Permanent failure was retried' }
            }
            Write-Output 'passed'
            """;
        foreach (var argument in new[] { "-NoProfile", "-Command", test }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        Assert.Equal(string.Empty, (await errors).Trim());
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("passed", (await output).Trim());
    }

    [Fact]
    public async Task AndroidRouteTimeDiagnosticsRecognizeOnlyClosedNonSecretFailureLabels()
    {
        var script = Path.Combine(FindRepository(), "eng", "Invoke-PhysicalDid2AccountProbeAndroid.ps1");
        var start = new System.Diagnostics.ProcessStartInfo("pwsh")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["DEEP_DIAGNOSTIC_SCRIPT"] = script;
        const string test = """
            $ErrorActionPreference='Stop'
            $tokens=$null;$errors=$null
            $ast=[Management.Automation.Language.Parser]::ParseFile($env:DEEP_DIAGNOSTIC_SCRIPT,[ref]$tokens,[ref]$errors)
            if ($errors.Count -ne 0) { throw 'Parser failed' }
            foreach ($name in @('Get-ProbeRouteTimeCoverageFailure','Get-ProbeNetworkOutcome')) {
                $functions=@($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name },$true))
                if ($functions.Count -ne 1) { throw 'Function scope failed' }
                . ([scriptblock]::Create($functions[0].Extent.Text))
            }
            $count=0
            foreach ($artifact in @('XNA1','XNV1','XNH1','ADH1','DeviceCertificate','DCA1','PMT2','XRA1','PMS2','XRC1','XSS1','XIR1V2','XRR1','ThresholdAuthoring','ResolveRequest')) {
                foreach ($boundary in @('InvalidInterval','NotBefore','Expiry')) {
                    $text="Не удалось проверить регистрацию: DID2 route time coverage failed ($artifact; $boundary). Локальный аккаунт сохранён."
                    $failure=Get-ProbeRouteTimeCoverageFailure $text
                    if ($failure.artifact -cne $artifact -or $failure.boundary -cne $boundary -or $failure.Count -ne 2) { throw 'Closed classification failed' }
                    if ((Get-ProbeNetworkOutcome $text) -cne 'route-time-coverage-rejected') { throw 'Failure cannot be success' }
                    $count++
                }
            }
            foreach ($text in @('DID2 route time coverage failed (SecretPayload; Expiry).','DID2 route time coverage failed (XRA1; Unknown).','DID2 route time coverage failed (XRA1; Expiry; private).','A route artifact does not cover the complete authenticated time interval.')) {
                if ($null -ne (Get-ProbeRouteTimeCoverageFailure $text)) { throw 'Unknown label escaped' }
            }
            if ((Get-ProbeNetworkOutcome 'Не удалось проверить регистрацию: неизвестная ошибка') -cne 'failure-redacted') { throw 'Unclassified failure became success' }
            Write-Output $count
            """;
        foreach (var argument in new[] { "-NoProfile", "-Command", test }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        Assert.Equal(string.Empty, (await errors).Trim());
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("45", (await output).Trim());
    }

    [Fact]
    public async Task WorkingTreeCompileCannotQualifyOrExecutePhysicalPublication()
    {
        var script = Path.Combine(FindRepository(), "eng", "Invoke-Did2HttpsWindowsBuild.ps1");
        var source = File.ReadAllText(script);
        Assert.Contains("(-not $CompileOnly -and $dirty.Count -ne 0)", source);
        Assert.Contains("compiled-working-tree-not-device-qualified", source);
        Assert.Contains("deviceDeliveryVerified = $false", source);
        Assert.DoesNotContain("CompileOnly", File.ReadAllText(Path.Combine(FindRepository(), "eng", "Invoke-Did2HttpsAndroidBuild.ps1")));
        var start = new System.Diagnostics.ProcessStartInfo("pwsh")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-File", script, "-ExpectedCommit", new string('1', 40),
            "-NetworkId", new string('1', 32), "-Xna1Pin", new string('1', 64), "-GenesisHeadPin", new string('1', 64),
            "-MrXPublicKeySha256", new string('1', 64), "-RegistryOrigin", "https://compile-only.invalid/",
            "-BootstrapDirectory", Path.GetTempPath(), "-RuntimeEnvironmentPath", Path.Combine(Path.GetTempPath(), "absent-input.env"),
            "-CompileOnly", "-Execute" }) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("Compile-only cannot publish", await error);
        Assert.DoesNotContain("Determining projects", await output);
    }

    [Fact]
    public void IncompatibleHttpsQaHasExplicitScopedResetButShippingStillCannotResetOnStartup()
    {
        var app = ReadSource("App.Did2.cs");
        var owner = ReadSource(Path.Combine("Services", "DeepIdV2AccountRuntimeOwner.cs"));
        const string guard = "#if DEEP_DID2_ACCOUNT_PROBE || DEEP_DID2_HTTPS_ADMISSION";
        Assert.Contains(guard, app); Assert.Contains(guard, owner);
        var guardedPage = app[app.IndexOf(guard, StringComparison.Ordinal)..app.IndexOf("#else", app.IndexOf(guard, StringComparison.Ordinal), StringComparison.Ordinal)];
        Assert.Contains("DisplayAlertAsync", guardedPage);
        Assert.True(guardedPage.IndexOf("DisplayAlertAsync", StringComparison.Ordinal) < guardedPage.IndexOf("ResetIsolatedProbeAfterConfirmationAsync", StringComparison.Ordinal));
        Assert.Contains("ResetExplicitlyAsync", owner);
        Assert.DoesNotContain("Remove-Item", owner);
        Assert.DoesNotContain("Directory.Delete", owner);
        Assert.Contains("Не удаляйте данные приложения", app);
    }

    [Fact]
    public void Did2ReconnectIncludesItsPlatformConnectivityAdapterAfterCleanServiceRemoval()
    {
        var project = System.Xml.Linq.XDocument.Parse(ReadSource("Deep.Client.Maui.csproj"));
        var adapter = Assert.Single(project.Descendants("Compile"), value =>
            (string?)value.Attribute("Include") == "Services\\MauiConnectivityStatusService.cs");
        Assert.Null(adapter.Attribute("Condition"));
        Assert.Null(adapter.Parent!.Attribute("Condition"));
        Assert.Contains("MauiConnectivityStatusService", ReadSource("MauiProgram.Did2.cs"));
        var source = ReadSource(Path.Combine("Services", "MauiConnectivityStatusService.cs"));
        Assert.Contains("INetworkStatusService", source);
        Assert.Contains("Connectivity.ConnectivityChanged", source);
        Assert.DoesNotContain("DeepAccountService", source);
    }

    [Fact]
    public void HttpsAndroidDiagnosticHasOnlySystemTrustAndExactPublicCaCrlHostException()
    {
        var config = System.Xml.Linq.XDocument.Parse(ReadSource(Path.Combine("Platforms", "Android", "Resources", "xml", "network_security_config_did2_https.xml")));
        var root = config.Root!;
        Assert.Equal("false", (string?)root.Element("base-config")!.Attribute("cleartextTrafficPermitted"));
        Assert.Equal("system", (string?)Assert.Single(root.Descendants("certificates")).Attribute("src"));
        var domain = Assert.Single(root.Elements("domain-config"));
        Assert.Equal("true", (string?)domain.Attribute("cleartextTrafficPermitted"));
        var host = Assert.Single(domain.Elements("domain"));
        Assert.Equal("false", (string?)host.Attribute("includeSubdomains"));
        Assert.Equal("c.pki.goog", host.Value);
        Assert.Equal(2, root.Elements().Count());
        var project = System.Xml.Linq.XDocument.Parse(ReadSource("Deep.Client.Maui.csproj"));
        var item = Assert.Single(project.Descendants("AndroidResource"), value =>
            (string?)value.Attribute("Include") == "Platforms\\Android\\Resources\\xml\\network_security_config_did2_https.xml");
        var condition = (string?)item.Parent!.Attribute("Condition");
        Assert.Contains("'$(DeepDid2HttpsAdmission)' == 'true'", condition);
        Assert.Contains("'$(DeepPhysicalE2E)' == 'true'", condition);
        Assert.Contains("'$(Configuration)' != 'Release'", condition);
    }

    [Fact]
    public void DefaultClientStartsDid2OwnerWithoutLegacyRuntime()
    {
        var project = ReadSource("Deep.Client.Maui.csproj");
        var startup = ReadSource("MauiProgram.Clean.cs");
        var did2 = ReadSource("MauiProgram.Did2.cs");
        var app = ReadSource("App.Did2.cs");

        Assert.Contains("Deep.Client.Maui.Core.Did2.csproj", project,
            StringComparison.Ordinal);
        Assert.DoesNotContain("<ProjectReference Include=\"..\\Deep.Client.Maui.Core\\Deep.Client.Maui.Core.csproj\"",
            project, StringComparison.Ordinal);
        Assert.Contains("<Compile Remove=\"Services\\**\\*.cs\" />", project,
            StringComparison.Ordinal);
        Assert.Contains("public static MauiApp CreateMauiApp() => CreateDid2MauiApp();",
            startup, StringComparison.Ordinal);
        Assert.Contains("new DeepIdV2AccountRuntimeAccessor(", did2,
            StringComparison.Ordinal);
        Assert.DoesNotContain("DeepAccountRuntimeAccessor", did2,
            StringComparison.Ordinal);
        Assert.Contains("#if DEEP_DID2_ACCOUNT_PROBE", app,
            StringComparison.Ordinal);
        Assert.Contains("Не удаляйте данные приложения", app,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Did2HttpsDiagnosticRequiresPhysicalUatAndCannotEnterRelease()
    {
        var project = ReadSource("Deep.Client.Maui.csproj");
        var startup = ReadSource("MauiProgram.Did2.cs");
        var admission = ReadSource(Path.Combine("Services",
            "DeepIdV2CanaryNetworkAdmission.cs"));

        Assert.Contains("Target Name=\"RejectUnsafeDid2HttpsAdmission\"", project,
            StringComparison.Ordinal);
        Assert.Contains("'$(DeepPhysicalE2E)' != 'true'", project,
            StringComparison.Ordinal);
        Assert.Contains("'$(Configuration)' == 'Release'", project,
            StringComparison.Ordinal);
        Assert.Contains("'$(DeepDid2AccountProbe)' == 'true'", project,
            StringComparison.Ordinal);
        Assert.Contains("#if DEEP_DID2_CANARY_ADMISSION || DEEP_DID2_HTTPS_ADMISSION",
            startup, StringComparison.Ordinal);
        Assert.Contains("Did2HttpsBuildTrust.RequireOrigin(Metadata(HttpsOriginKey), PrivateCaPin())",
            admission, StringComparison.Ordinal);
        Assert.Contains("DeepIdV2DirectoryBootstrapVerifier.RestoreGenesis",
            admission, StringComparison.Ordinal);
        Assert.Contains("CreateDeepIdV2NetworkClosureArtifactSource(origin,",
            admission, StringComparison.Ordinal);
        Assert.Contains("PooledConnectionLifetime: TimeSpan.FromTicks(1)", admission,
            StringComparison.Ordinal);
        Assert.Contains("accounts.OpenNetworkLkgStoreAsync(genesisPin,",
            admission, StringComparison.Ordinal);
        var accountVerification = admission[..admission.IndexOf(
            "private static HttpServiceClientOptions? DiagnosticHttpOptions()", StringComparison.Ordinal)];
        Assert.DoesNotContain("networkSource.VerifyCurrentNetworkAsync(",
            accountVerification, StringComparison.Ordinal);
        Assert.DoesNotContain("accounts.EnsureOwnPreKeyInventoryAsync(", admission, StringComparison.Ordinal);
        Assert.DoesNotContain("accounts.OpenOwnOnionClientCustodyAsync(cancellationToken)",
            accountVerification, StringComparison.Ordinal);
        Assert.DoesNotContain("accounts.PublishOwnPreKeyInventoryAsync(networkSource,",
            accountVerification, StringComparison.Ordinal);
        Assert.Contains("HttpStageAsync(\"AccountProof\"", admission);
        Assert.DoesNotContain("HttpStageAsync(\"NetworkVerification\"", accountVerification);
        Assert.DoesNotContain("HttpStageAsync(\"PreKeyPublication\"", accountVerification);
        Assert.Contains("HttpStageAsync(\"ContactPublication\"", admission);
        Assert.Contains("accounts.EnsureOwnPermanentContactPublishedAsync(networkSource,", admission);
        Assert.Contains("HttpStageAsync(\"ContactResolution\"", admission);
        Assert.Contains("accounts.ResolvePermanentContactAsync(descriptor, source,", admission);
        Assert.Contains("resolved.Candidate.ExactDid2.CanonicalBytes.Span", admission);
        Assert.Contains("exception.HttpRequestError", admission);
        Assert.Contains("PooledConnectionLifetime: TimeSpan.FromTicks(1), UseSystemProxy: true", admission);
        Assert.Contains("<Compile Include=\"Services\\Did2TlsFailureClassifier.cs\" />", project,
            StringComparison.Ordinal);
        Assert.Contains("Did2TlsFailureClassifier.Classify(exception)", admission,
            StringComparison.Ordinal);
        Assert.Contains("chain == \"Unknown\" && PrivateCaPin().Length == 0", admission);
        Assert.Contains("chain = \"PrivateProfileUnknown\"", admission);
        Assert.Contains("catch (TimeoutException exception)", admission);
        Assert.Contains("DID2 {stage} failed (Timeout).", admission);
        Assert.Contains("catch (IOException exception)", admission);
        Assert.Contains("Did2NetworkIoFailure.AtStage(stage, exception)", admission);
        Assert.Contains("<Compile Include=\"Services\\Did2NetworkIoFailure.cs\" />", project);
        Assert.DoesNotContain("new PrivacyRoutedContactResolverTransport", admission,
            StringComparison.Ordinal);
        // Shape/composition guard only; real signature, freshness and floor
        // behavior is exercised by the Shared native/SQLCipher fixture.
        Assert.Contains("clock, verifier, protectedFloor, clientOptions: publicClientOptions)", admission,
            StringComparison.Ordinal);
        Assert.Contains("accounts, proof, closure, networkFloor, clock)", admission,
            StringComparison.Ordinal);
    }

    [Fact]
    public void HttpsPhysicalBuildRejectsRetiredTrustInputsAndUsesSeparateWindowsCustody()
    {
        var project = System.Xml.Linq.XDocument.Parse(ReadSource("Deep.Client.Maui.csproj"));
        var legacyGate = project.Root!.Elements("Target").Single(value =>
            (string?)value.Attribute("Name") == "ValidatePhysicalUatMailboxInputs");
        Assert.Contains("'$(DeepDid2HttpsAdmission)' != 'true'",
            (string?)legacyGate.Attribute("Condition"));
        var did2Gate = project.Root.Elements("Target").Single(value =>
            (string?)value.Attribute("Name") == "RejectUnsafeDid2HttpsAdmission");
        Assert.Contains(did2Gate.Elements("Error"), value =>
            ((string?)value.Attribute("Text"))?.Contains("retired mailbox/PMA",
                StringComparison.Ordinal) == true);
        Assert.Contains(did2Gate.Elements("Error"), value =>
            ((string?)value.Attribute("Text"))?.Contains("explicit nonzero network ID",
                StringComparison.Ordinal) == true);
        var paths = ReadSource(Path.Combine("Services", "AppDataPath.cs"));
        Assert.Contains("#if DEEP_DID2_HTTPS_ADMISSION && WINDOWS", paths);
        Assert.Contains("#elif DEEP_DID2_HTTPS_ADMISSION && ANDROID", paths);
        Assert.Contains("context.DataDir?.CanonicalPath", paths);
        Assert.Contains("context.FilesDir?.CanonicalPath", paths);
        Assert.Contains("Path.Combine(sandbox, \"files\")", paths);
        Assert.Contains("Did2ProbeStorageScope.RequireOwnedPhysicalPath(appDataRoot, physicalRoot)", paths);
        Assert.Contains("Did2ProbeStorageScope.ResolvePhysicalRoot(", paths);
        Assert.True(paths.IndexOf("RejectReparsePoints(physicalRoot)", StringComparison.Ordinal)
            < paths.IndexOf("Directory.CreateDirectory(physicalRoot)", StringComparison.Ordinal));
        var script = File.ReadAllText(Path.Combine(FindRepository(), "eng",
            "Invoke-Did2HttpsWindowsBuild.ps1"));
        Assert.Contains("$commit -cne $ExpectedCommit", script);
        Assert.Contains("$dirty.Count -ne 0", script);
        Assert.Contains("[switch]$Execute", script);
        Assert.Contains("'-p:DeepLocalDev=false'", script);
        Assert.Contains("'-p:DeepDid2AccountProbe=false'", script);
        Assert.Contains("'-p:WindowsPackageType=None'", script);
        Assert.DoesNotContain("Pma", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Start-Process", script);
        Assert.DoesNotContain("Remove-Item", script);
    }

    [Fact]
    public void Did2HttpsAndroidUsesSeparatePackageAndDefaultSystemTrust()
    {
        var project = System.Xml.Linq.XDocument.Parse(ReadSource("Deep.Client.Maui.csproj"));
        var package = project.Descendants("ApplicationId").Single(value =>
            value.Value == "network.xpoint.deep.did2https");
        Assert.Contains("'$(DeepDid2HttpsAdmission)' == 'true'", (string?)package.Attribute("Condition"));
        Assert.Contains("'$(Configuration)' != 'Release'", (string?)package.Attribute("Condition"));
        var physicalCa = project.Root!.Elements("ItemGroup").Single(group =>
            group.Elements("AndroidResource").Any(value =>
                (string?)value.Attribute("Include") == "Platforms\\Android\\Resources\\raw\\deep_physical_uat_ca.crt"));
        Assert.Contains("'$(DeepDid2HttpsAdmission)' != 'true'", (string?)physicalCa.Attribute("Condition"));
        var builder = File.ReadAllText(Path.Combine(FindRepository(), "eng", "Invoke-Did2HttpsAndroidBuild.ps1"));
        Assert.Contains("Invoke-Did2HttpsWindowsBuild.ps1", builder);
        Assert.Contains("'--ks-pass', \"file:$passwordFile\"", builder);
        Assert.Contains("'--ks-type', 'PKCS12'", builder);
        Assert.DoesNotContain("AndroidSigningKeyPass=", builder);
        Assert.DoesNotContain("'--key-pass'", builder);
        Assert.Contains("android-arm64\\network.xpoint.deep.did2https-Signed.apk", builder);
        Assert.Contains("$Matches[1] -cne $ExpectedSignerSha256", builder);
        Assert.DoesNotContain("pm clear", builder);
        var installer = File.ReadAllText(Path.Combine(FindRepository(), "eng", "Invoke-PhysicalDid2AccountProbeAndroid.ps1"));
        Assert.Contains("[ValidateSet('Did2Account', 'Did2Https')]", installer);
        Assert.Contains("'network.xpoint.deep.did2https'", installer);
        Assert.Contains("Assert-SameSnapshot $before[$package] $after[$package] $package", installer);
        Assert.Contains("'Inspect', 'SetName', 'DismissKeyboard', 'CreateAccount', 'Settings', 'ScrollSettings', 'VerifyNetwork', 'Restart'", installer);
        Assert.Contains("$Lane -ne 'Did2Https'", installer);
        Assert.Contains("mCurrentFocus=", installer);
        Assert.Contains("@('shell', 'dumpsys', 'window')", installer);
        Assert.DoesNotContain("@('shell', 'dumpsys', 'window', 'windows')", installer);
        Assert.Contains("$settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit", installer);
        Assert.Contains("$field.Count -ne 1", installer);
        Assert.Contains("$result.uiAfter = $(if ($Phase -eq 'Inspect') { (Read-ProbeUi).Summary }", installer);
        Assert.Contains("else { (Read-ProbeUiAfterAction).Summary })", installer);
        Assert.Contains("networkOutcome=(Get-ProbeNetworkOutcome $text)", installer);
        Assert.Contains("return 'verified-publication'", installer);
        Assert.Contains("return 'proof-authority-unavailable'", installer);
        Assert.Contains("return 'failure-redacted'", installer);
        Assert.Contains("Click-ProbeControl $ui 'Welcome.CreateAccount'", installer);
        Assert.DoesNotContain("Click-ProbeControl $ui 'Welcome.Create'", installer);
        Assert.Contains("merge-base --is-ancestor $apkSourceCommit $commit", installer);
        Assert.Contains("$Phase -eq 'Install'", installer);
        Assert.Contains("$_ -cnotin $harnessOnly", installer);
        Assert.Contains("'docs/S05-BUSINESS-WORK-2026-10-17.md'", installer);
        Assert.DoesNotContain("$changed | Where-Object { $_ -like 'docs/*'", installer);
        Assert.Contains("commit = $apkSourceCommit", installer);
        Assert.Contains("harnessCommit = $commit", installer);
        Assert.Contains("$Ui.ImeShowing -and $Id -cne 'Welcome.DisplayName'", installer);
        Assert.Contains("'DismissKeyboard'", installer);
        Assert.Contains("'Did2Workspace.MobileSettings'", installer);
        Assert.Contains("'ScrollSettings'", installer);
        Assert.Contains("'ScrollSettingsUp'", installer);
        Assert.Contains("'ScrollContacts'", installer);
        Assert.Contains("$Phase -ceq 'ScrollContacts'", installer);
        Assert.Contains("'Page.Contacts'", installer);
        Assert.Contains("$Phase -eq 'ScrollSettingsUp'", installer);
        Assert.Contains("(($bottom-$top)/3)", installer);
        Assert.DoesNotContain("[string]($bottom-100)", installer);
        Assert.Contains("'BeginReset', 'ConfirmReset'", installer);
        Assert.Contains("ConfirmReset requires explicit -ConfirmIsolatedAccountReset.", installer);
        Assert.Contains("$Ui.Summary.resetConfirmationVisible", installer);
        Assert.Contains("'android:id/button1'", installer);
        Assert.Contains("'Удалить тестовый аккаунт'", installer);
        Assert.Contains("Test-OwnedResetDialogFocus $focus $packageOwner", installer);
        Assert.Contains("mOwnerUid=(?<uid>", installer);
        Assert.Contains("$ownedResetDialog -and -not $resetConfirmationVisible", installer);
        Assert.Contains("$paneId = if ($Phase -ceq 'ScrollContacts') { 'Page.Contacts' } else { 'Page.Settings' }", installer);
        Assert.Contains("${probePackage}:id/$paneId", installer);
        Assert.Contains("'android.widget.ScrollView'", installer);
        Assert.DoesNotContain("uninstall", installer);
        Assert.DoesNotContain("pm clear", installer);
    }

    [Fact]
    public void PhysicalAndroidQaUsesGuardedOptimizedJitWithoutBecomingRelease()
    {
        var project = System.Xml.Linq.XDocument.Parse(ReadSource("Deep.Client.Maui.csproj"));
        var profile = Assert.Single(project.Root!.Elements("PropertyGroup"),
            element => element.Element("AndroidUseInterpreter") is not null);
        var condition = (string?)profile.Attribute("Condition") ?? string.Empty;
        Assert.Contains("'$(DeepDid2HttpsAdmission)' == 'true'", condition);
        Assert.Contains("'$(DeepPhysicalE2E)' == 'true'", condition);
        Assert.Contains("'$(Configuration)' != 'Release'", condition);
        Assert.Contains("== 'android'", condition);
        Assert.Equal("false", profile.Element("UseInterpreter")!.Value);
        Assert.Equal("false", profile.Element("AndroidUseInterpreter")!.Value);
        Assert.Equal("true", profile.Element("Optimize")!.Value);
        var guard = Assert.Single(project.Root.Elements("Target"),
            element => (string?)element.Attribute("Name") == "RequireDid2AndroidQaJit");
        Assert.Equal(condition, (string?)guard.Attribute("Condition"));
        Assert.Contains("$(UseInterpreter)", (string?)guard.Element("Error")!.Attribute("Condition"));
        var builder = File.ReadAllText(Path.Combine(FindRepository(), "eng", "Invoke-Did2HttpsAndroidBuild.ps1"));
        Assert.Contains("'-p:UseInterpreter=false'", builder);
        Assert.Contains("'-p:AndroidUseInterpreter=false'", builder);
        Assert.Contains("'-p:Optimize=true'", builder);
        Assert.Contains("managedExecutionProfile = 'optimized-jit'", builder);
        Assert.Contains("'--no-incremental'", builder);
        Assert.Contains("incrementalBuild = $false", builder);
        Assert.Contains("releaseEvidence = $false", builder);
        var google = Assert.Single(project.Descendants("GoogleServicesJson"));
        var googleCondition = (string?)google.Attribute("Condition");
        Assert.Contains("'$(DeepDid2HttpsAdmission)' != 'true' Or '$(Configuration)' == 'Release'", googleCondition);
        Assert.Contains(project.Root.Elements("Target"), element =>
            (string?)element.Attribute("Name") == "EnsureGoogleServicesJson" &&
            ((string?)element.Attribute("Condition"))!.Contains("'$(Configuration)' == 'Release'", StringComparison.Ordinal));
    }

    [Fact]
    public void ExplicitPrivateCaIsBoundedPinnedAndCannotEnterReleaseOrLoopbackProbe()
    {
        var project = System.Xml.Linq.XDocument.Parse(ReadSource("Deep.Client.Maui.csproj"));
        var group = project.Root!.Elements("ItemGroup").Single(value => value.Elements("MauiAsset")
            .Any(asset => (string?)asset.Attribute("LogicalName") == "did2_private_ca.bin"));
        var condition = (string?)group.Attribute("Condition");
        Assert.Contains("'$(DeepDid2HttpsAdmission)' == 'true'", condition);
        Assert.Contains("'$(DeepPhysicalE2E)' == 'true'", condition);
        Assert.Contains("'$(Configuration)' != 'Release'", condition);
        Assert.Contains(group.Elements("AssemblyMetadata"), value =>
            (string?)value.Attribute("Include") == "DeepDid2PrivateCaSha256");
        var gate = project.Root.Elements("Target").Single(value =>
            (string?)value.Attribute("Name") == "RejectUnsafeDid2PrivateCertificateAuthority");
        Assert.Contains(gate.Elements("Error"), value =>
            ((string?)value.Attribute("Condition"))?.Contains("'$(Configuration)' == 'Release'", StringComparison.Ordinal) == true);
        Assert.Contains(gate.Elements("Error"), value =>
            ((string?)value.Attribute("Condition"))?.Contains("!Exists('$(DeepDid2PrivateCertificateAuthority)')", StringComparison.Ordinal) == true);
        var admission = ReadSource(Path.Combine("Services", "DeepIdV2CanaryNetworkAdmission.cs"));
        Assert.Contains("Did2HttpsBuildTrust.RequirePublicRoot(encoded, pin)", admission);
        Assert.Contains("WithAppScopedPrivateCertificateAuthority(der)", admission);
        Assert.Equal(2, admission.Split("await CreateHttpFactoryAsync(cancellationToken)").Length - 1);
        var messaging = ReadSource(Path.Combine("Services", "DeepIdV2CanaryNetworkAdmission.Messaging.cs"));
        Assert.Contains("await CreateHttpFactoryAsync(ct)", messaging);
        foreach (var name in new[] { "Invoke-Did2HttpsWindowsBuild.ps1", "Invoke-Did2HttpsAndroidBuild.ps1" })
        {
            var script = File.ReadAllText(Path.Combine(FindRepository(), "eng", name));
            Assert.Contains("[string]$PrivateCertificateAuthorityPath", script);
            Assert.Contains("-p:DeepDid2PrivateCertificateAuthority=", script);
            Assert.Contains("-p:DeepDid2PrivateCaSha256=", script);
        }
    }

    [Fact]
    public void Did2AppDoesNotShipUnusedRetiredCmsAttestationPackage()
    {
        var project = System.Xml.Linq.XDocument.Parse(ReadSource("Deep.Client.Maui.csproj"));
        Assert.DoesNotContain(project.Descendants("PackageReference"), value =>
            (string?)value.Attribute("Include") == "System.Security.Cryptography.Pkcs");
        Assert.Contains(project.Descendants("Compile"), value =>
            (string?)value.Attribute("Remove") == "Services\\ProductionMailboxClientIdentityAttestor.cs");
    }

    [Fact]
    public void PrivateAndroidCrlPolicyHasOneExactHostAndNoAddedTrustOrGlobalCleartext()
    {
        var template = File.ReadAllText(Path.Combine(FindRepository(), "eng", "did2-private-ca-network-security.xml"));
        Assert.Equal(1, template.Split("__DEEP_PRIVATE_CRL_HOST__").Length - 1);
        var policy = System.Xml.Linq.XDocument.Parse(template.Replace("__DEEP_PRIVATE_CRL_HOST__", "dev.example"));
        var root = policy.Root!;
        Assert.Equal(2, root.Elements().Count());
        Assert.Equal("false", (string?)root.Element("base-config")!.Attribute("cleartextTrafficPermitted"));
        var anchor = Assert.Single(root.Descendants("certificates"));
        Assert.Equal("system", (string?)anchor.Attribute("src"));
        var domain = Assert.Single(root.Elements("domain-config"));
        Assert.Equal("true", (string?)domain.Attribute("cleartextTrafficPermitted"));
        var host = Assert.Single(domain.Elements());
        Assert.Equal("domain", host.Name.LocalName);
        Assert.Equal("false", (string?)host.Attribute("includeSubdomains"));
        Assert.Equal("dev.example", host.Value);
        var project = System.Xml.Linq.XDocument.Parse(ReadSource("Deep.Client.Maui.csproj"));
        var group = project.Root!.Elements("ItemGroup").Single(value => value.Elements("AndroidResource").Any(resource =>
            (string?)resource.Attribute("Include") == "$(DeepDid2PrivateCaAndroidNetworkSecurityConfig)"));
        var condition = (string?)group.Attribute("Condition");
        foreach (var required in new[] { "'$(DeepDid2HttpsAdmission)' == 'true'", "'$(DeepPhysicalE2E)' == 'true'",
            "'$(Configuration)' != 'Release'", "'$(DeepDid2PrivateCertificateAuthority)' != ''" }) Assert.Contains(required, condition);
        var gate = project.Root.Elements("Target").Single(value => (string?)value.Attribute("Name") == "RejectUnsafeDid2PrivateCaAndroidPolicy");
        Assert.Contains(gate.Elements("Error"), value => ((string?)value.Attribute("Condition"))?.Contains("'$(Configuration)' == 'Release'", StringComparison.Ordinal) == true);
        var builder = File.ReadAllText(Path.Combine(FindRepository(), "eng", "Invoke-Did2HttpsAndroidBuild.ps1"));
        Assert.Contains("$privateCrlHost = $privateOrigin.DnsSafeHost", builder);
        Assert.Contains("[Security.SecurityElement]::Escape($privateCrlHost)", builder);
        Assert.Contains("-p:DeepDid2PrivateCaAndroidNetworkSecurityConfig=$privateConfig", builder);
    }

    [Fact]
    public void AdmissionAndConversationVerifyFullPinnedAuthorityHistoryBeforeDirectoryProof()
    {
        var source = ReadSource(Path.Combine("Services", "DeepIdV2CanaryNetworkAdmission.cs"));
        Assert.Contains("factory, origin, cancellationToken)", source);
        var bootstrap = source[source.IndexOf("byte[] ExactHead, byte[] HeadPin)> ReadBootstrapAsync(", StringComparison.Ordinal)..];
        Assert.Contains("HttpStageAsync(\"BootstrapAuthority\"", bootstrap);
        Assert.Contains("history.ExactXna1AuthorityChain[0].Span.SequenceEqual(exactXna)", bootstrap);
        Assert.Contains("history.ExactDts1PolicyChain[0].Span.SequenceEqual(exactDts)", bootstrap);
        Assert.Contains("history.ExactXna1AuthorityChain, history.ExactDts1PolicyChain)", bootstrap);
        Assert.Contains("RestoreGenesis(authority, exactHead, headPin)", bootstrap);
        Assert.DoesNotContain("EnsureCurrent()", bootstrap);
        var messaging = ReadSource(Path.Combine("Services", "DeepIdV2CanaryNetworkAdmission.Messaging.cs"));
        Assert.Contains("ReadBootstrapAsync(factory, origin, ct)", messaging);
        var admission = source[..source.IndexOf("private static HttpServiceClientOptions?", StringComparison.Ordinal)];
        Assert.True(admission.IndexOf("ReadBootstrapAsync(", StringComparison.Ordinal) <
            admission.IndexOf("accounts.AdmitAndVerifyGenesisAsync(", StringComparison.Ordinal));
        Assert.Contains("accounts.EnsureOwnPermanentContactPublishedAsync(networkSource,", admission);
        var shared = Path.Combine(Path.GetDirectoryName(FindRepository())!, "deep-client-shared",
            "src", "Deep.Client.Shared", "Services");
        var workflow = File.ReadAllText(Path.Combine(shared, "DeepIdV2AccountService.ContactRoute.cs"));
        var command = workflow[workflow.IndexOf("public async Task<VerifiedDeepIdV2PublicationCommit> EnsureOwnPermanentContactPublishedAsync(",
            StringComparison.Ordinal)..workflow.IndexOf("internal async Task<VerifiedDeepIdV2PublicationCommit> EnsureOwnPermanentContactPublishedAsync(",
            StringComparison.Ordinal)];
        Assert.Contains("source.VerifyForOwnPreKeyAuthoringAsync(this,", command);
        Assert.True(command.IndexOf("source.VerifyForOwnPreKeyAuthoringAsync(", StringComparison.Ordinal) <
            command.IndexOf("PublishOwnPreKeyInventoryCoreAsync(", StringComparison.Ordinal));
        Assert.Contains("EnsureOwnPermanentContactPublishedCoreAsync(", command);
        Assert.Contains("await source.RecheckOwnPreKeyAuthoringAsync(fresh, ct)", workflow);
        var authority = File.ReadAllText(Path.Combine(shared, "ContactV2", "DeepIdV2ContactPathAuthoritySource.cs"));
        Assert.Contains("verified.EnsureCurrent()", authority);
    }

    [Fact]
    public void AndroidIncompatibleAccountResetIsOwnedExplicitAndSeparateFromSettingsReset()
    {
        var installer = File.ReadAllText(Path.Combine(FindRepository(), "eng", "Invoke-PhysicalDid2AccountProbeAndroid.ps1"));
        Assert.Contains("'BeginIncompatibleReset', 'ConfirmIncompatibleReset'", installer);
        Assert.Contains("$Phase -in @('ConfirmReset', 'ConfirmIncompatibleReset') -and -not $ConfirmIsolatedAccountReset", installer);
        Assert.Contains("$ConfirmIsolatedAccountReset -and $Phase -notin", installer);
        Assert.Contains("'Startup.Error', 'Startup.ResetIncompatibleDid2'", installer);
        Assert.Contains("$owner.Groups['uid'].Value -ceq $uid.Groups['uid'].Value", installer);
        Assert.Contains("$ownedResetDialog -and -not $resetConfirmationVisible -and -not $incompatibleResetConfirmationVisible", installer);
        Assert.Contains("$startupReset -and -not $Ui.Summary.incompatibleResetConfirmationVisible", installer);
        Assert.Contains("-not $startupReset -and -not $Ui.Summary.resetConfirmationVisible", installer);
        Assert.Contains("$confirmText = if ($startupReset) { 'Удалить' } else { 'Удалить тестовый аккаунт' }", installer);
        Assert.Contains("Click-ProbeControl $ui 'Startup.ResetIncompatibleDid2'", installer);
        Assert.Contains("Click-ProbeControl $ui 'Startup.ConfirmIncompatibleReset'", installer);
        Assert.Contains("ContactPublication|ContactResolution", installer);
        Assert.DoesNotContain("pm clear", installer);
        Assert.DoesNotContain("uninstall", installer);
    }

    [Fact]
    public void WindowsUatPackageCanAdvanceRevisionWithoutChangingProductionVersion()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "eng",
                "Invoke-PhysicalUatWindowsMsix.ps1");
            if (!File.Exists(candidate)) continue;
            var script = File.ReadAllText(candidate);
            Assert.Contains("[int]$PackageRevision", script, StringComparison.Ordinal);
            Assert.Contains("$PSBoundParameters.ContainsKey('PackageRevision')", script,
                StringComparison.Ordinal);
            Assert.Contains("[int]$versionCode", script, StringComparison.Ordinal);
            return;
        }
        throw new FileNotFoundException("Windows UAT build script is unavailable.");
    }

    [Fact]
    public void AndroidReleasePrivacyScreenDoesNotDependOnRetiredSettings()
    {
        var activity = ReadSource(Path.Combine("Platforms", "Android", "MainActivity.cs"));
        Assert.Contains("Window.AddFlags(WindowManagerFlags.Secure);", activity,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ClientSettingKeys.PrivacyScreenSecurity", activity,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Preferences.Default.Get", activity,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidClearsNativeInputFocusBeforeMauiActivityPause()
    {
        var activity = ReadSource(Path.Combine("Platforms", "Android", "MainActivity.cs"));
        var pauseStart = activity.IndexOf("protected override void OnPause()",
            StringComparison.Ordinal);
        var pauseEnd = activity.IndexOf("private void ApplyPrivacyScreenSetting()",
            pauseStart, StringComparison.Ordinal);
        Assert.True(pauseStart >= 0 && pauseEnd > pauseStart);
        var pause = activity[pauseStart..pauseEnd];
        var clearFocus = pause.IndexOf("CurrentFocus?.ClearFocus();",
            StringComparison.Ordinal);
        var basePause = pause.IndexOf("base.OnPause();", StringComparison.Ordinal);
        Assert.True(clearFocus >= 0 && basePause > clearFocus);
    }

    private static string ReadSource(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Deep.Client.Maui", name);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException($"MAUI source {name} is unavailable.");
    }

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "eng",
                    "Invoke-Did2HttpsWindowsBuild.ps1"))) return directory.FullName;
        throw new FileNotFoundException("DID2 Windows build script is unavailable.");
    }
}
