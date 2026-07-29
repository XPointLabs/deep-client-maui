namespace Deep.Client.Maui.UiTests;

public sealed class StrictCrossPlatformContractsTests
{
    [Fact]
    public void Resource_id_lookup_requires_one_exact_node_and_derives_its_center()
    {
        const string xml = "<hierarchy><node resource-id='network.xpoint.deep.e2e:id/Chat_Send' text='' bounds='[20,40][100,80]' /></hierarchy>";

        var node = StrictCrossPlatformContracts.FindExactlyOneResourceId(xml, "network.xpoint.deep.e2e:id/Chat_Send");

        Assert.Equal((60, 60), node.Bounds.Center);
    }

    [Fact]
    public void Resource_id_lookup_rejects_missing_or_ambiguous_ids()
    {
        const string empty = "<hierarchy />";
        const string duplicate = "<hierarchy><node resource-id='pkg:id/a' bounds='[0,0][1,1]' /><node resource-id='pkg:id/a' bounds='[1,1][2,2]' /></hierarchy>";

        Assert.Throws<InvalidOperationException>(() => StrictCrossPlatformContracts.FindExactlyOneResourceId(empty, "pkg:id/a"));
        Assert.Throws<InvalidOperationException>(() => StrictCrossPlatformContracts.FindExactlyOneResourceId(duplicate, "pkg:id/a"));
        Assert.Throws<InvalidOperationException>(() => StrictCrossPlatformContracts.ValidateResourceId("Chat.Send", "resource-id"));
    }

    [Fact]
    public void Dynamic_picker_item_still_requires_an_exact_resource_id_before_its_bounds_are_used()
    {
        const string xml = "<hierarchy><node resource-id='com.android.documentsui:id/item' text='strict-file.bin' bounds='[4,8][8,16]' /></hierarchy>";

        var node = StrictCrossPlatformContracts.FindExactlyOneResourceIdWithText(xml, "com.android.documentsui:id/item", "strict-file.bin");

        Assert.Equal((6, 12), node.Bounds.Center);
    }

    [Fact]
    public void Repeated_message_body_nodes_match_the_unique_marker_not_the_unique_resource_id()
    {
        const string xml = "<hierarchy><node resource-id='network.xpoint.deep.e2e:id/message' text='older' bounds='[0,0][2,2]' /><node resource-id='network.xpoint.deep.e2e:id/message' text='marker-123' bounds='[2,2][4,4]' /></hierarchy>";

        var node = StrictCrossPlatformContracts.FindExactlyOneResourceIdContainingText(xml, "network.xpoint.deep.e2e:id/message", "marker-123");

        Assert.Equal("marker-123", node.Text);
        Assert.Throws<InvalidOperationException>(() => StrictCrossPlatformContracts.FindExactlyOneResourceId(xml, "network.xpoint.deep.e2e:id/message"));
    }

    [Fact]
    public void Bounds_and_process_restart_contracts_fail_closed()
    {
        Assert.Throws<InvalidOperationException>(() => StrictCrossPlatformContracts.AndroidBounds.Parse("[9,9][9,10]"));
        Assert.Throws<InvalidOperationException>(() => StrictCrossPlatformContracts.AssertDistinctProcessIds(42, 42));
        StrictCrossPlatformContracts.AssertDistinctProcessIds(42, 43);
    }

    [Fact]
    public void Apk_metadata_and_identity_are_strictly_parsed()
    {
        var metadata = StrictCrossPlatformContracts.ApkMetadata.ParseAaptBadging("package: name='network.xpoint.deep.e2e' versionCode='1' versionName='1.2.3'\n", new string('a', 64));

        Assert.Equal(StrictCrossPlatformContracts.AndroidPackage, metadata.PackageName);
        Assert.Equal("1", metadata.VersionCode);
        Assert.Equal("1.2.3", metadata.VersionName);
        Assert.Equal("25" + new string('b', 64), StrictCrossPlatformContracts.RequireSessionId("25" + new string('b', 64), "test"));
        Assert.Throws<InvalidOperationException>(() => StrictCrossPlatformContracts.RequireSessionId(new string('b', 64), "test"));
        Assert.Throws<InvalidOperationException>(() => StrictCrossPlatformContracts.RequireSessionId("35" + new string('b', 64), "test"));
        Assert.Throws<InvalidOperationException>(() => StrictCrossPlatformContracts.RequireSessionId("not-an-id", "test"));
    }

    [Fact]
    public void Valid_unknown_identity_is_not_permitted_as_a_negative_validation_fixture()
    {
        var validUnknown = "05" + new string('a', 64);

        Assert.Equal(validUnknown, StrictCrossPlatformContracts.RequireSessionId(validUnknown, "unknown peer"));
        Assert.Throws<InvalidOperationException>(() => StrictCrossPlatformContracts.RequireInvalidSessionId(validUnknown));
        StrictCrossPlatformContracts.RequireInvalidSessionId("35" + new string('a', 64));
    }

    [Fact]
    public void Caller_controlled_tool_apk_commit_and_virtual_device_mutations_fail_closed()
    {
        var currentAssembly = typeof(StrictCrossPlatformContractsTests).Assembly.Location;

        Assert.Throws<InvalidOperationException>(() =>
            StrictCrossPlatformContracts.RequirePinnedFile(currentAssembly, new string('0', 64), "mutated tool/APK"));
        Assert.Throws<InvalidOperationException>(() =>
            StrictCrossPlatformContracts.RequireCurrentCommit(
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")),
                new string('0', 40)));
        Assert.Throws<InvalidOperationException>(() =>
            StrictCrossPlatformContracts.RequirePhysicalDeviceInventory(
                "generic/sdk_gphone", "Android SDK built for x86", "sdk_gphone", "ranchu", "emulator", 35));
    }

    [Fact]
    public void Bounded_process_rejects_hang_and_nonzero_version_exit()
    {
        var command = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");

        Assert.Throws<TimeoutException>(() =>
            StrictCrossPlatformContracts.RunBounded(
                command,
                ["-NoProfile", "-Command", "Start-Sleep -Seconds 30"],
                TimeSpan.FromMilliseconds(200)));
        var failed = StrictCrossPlatformContracts.RunBounded(command, ["-NoProfile", "-Command", "exit 7"], TimeSpan.FromSeconds(5));
        Assert.Equal(7, failed.ExitCode);
        Assert.Throws<InvalidOperationException>(() =>
            StrictCrossPlatformContracts.RequireExactVersion(failed, "expected", "mutated tool"));
    }

    [Fact]
    public void Bounded_process_rejects_exited_parent_when_descendant_holds_redirected_pipes()
    {
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var command =
            "$child = Start-Process -PassThru -NoNewWindow -FilePath '" + powershell.Replace("'", "''", StringComparison.Ordinal) +
            "' -ArgumentList @('-NoProfile','-Command','while ($true) { Write-Output held; Start-Sleep -Milliseconds 20 }'); exit 0";
        var started = DateTime.UtcNow;

        Assert.Throws<TimeoutException>(() =>
            StrictCrossPlatformContracts.RunBounded(
                powershell,
                ["-NoProfile", "-Command", command],
                TimeSpan.FromMilliseconds(400)));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(4));
    }

    [Fact]
    public void Downloads_snapshot_accepts_production_collision_and_never_owns_preexisting_file()
    {
        var root = Path.Combine(Path.GetTempPath(), $"deep-download-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var preexisting = Path.Combine(root, "fixture.bin");
        var collision = Path.Combine(root, "fixture (2).bin");
        try
        {
            File.WriteAllText(preexisting, "preexisting");
            var snapshot = StrictCrossPlatformContracts.SnapshotDownloads(root);
            File.WriteAllText(collision, "run-created");

            Assert.Equal(collision, snapshot.WaitForNewCorrelatedFile("fixture.bin", TimeSpan.FromSeconds(1)));
            Assert.Contains(preexisting, snapshot.Preexisting);
            File.Delete(collision);
            Assert.True(File.Exists(preexisting));
        }
        finally
        {
            if (File.Exists(collision)) File.Delete(collision);
            if (File.Exists(preexisting)) File.Delete(preexisting);
            if (Directory.Exists(root)) Directory.Delete(root);
        }
    }

    [Fact]
    public void Cleanup_attempts_every_step_and_aggregates_failures()
    {
        var firstAttempted = false;
        var lastAttempted = false;
        var cleanup = new StrictCrossPlatformContracts.CleanupScope();
        cleanup.Add(() => { firstAttempted = true; throw new IOException("first"); });
        cleanup.Add(() => throw new UnauthorizedAccessException("second"));
        cleanup.Add(() => lastAttempted = true);

        var error = Assert.Throws<AggregateException>(() => cleanup.RunAll());

        Assert.True(firstAttempted);
        Assert.True(lastAttempted);
        Assert.Equal(2, error.InnerExceptions.Count);
    }

    [Theory]
    [InlineData("cold-start")]
    [InlineData("identity")]
    [InlineData("push")]
    public void Partial_android_attempts_are_cleanup_eligible_before_the_fault(string faultStage)
    {
        var packageCleanup = 0;
        var fixtureCleanup = 0;
        var cleanup = new StrictCrossPlatformContracts.CleanupScope();
        var state = new StrictCrossPlatformContracts.AttemptCleanupState();
        state.Register(cleanup, () => packageCleanup++, () => fixtureCleanup++);

        state.BeginAndroidPackageMutation();
        if (faultStage == "push") state.BeginFixturePush();
        Exception? captured = null;
        try { throw new IOException(faultStage); }
        catch (Exception exception) { captured = exception; }

        Assert.IsType<IOException>(captured);
        cleanup.RunAll();
        Assert.Equal(1, packageCleanup);
        Assert.Equal(faultStage == "push" ? 1 : 0, fixtureCleanup);
    }
}
