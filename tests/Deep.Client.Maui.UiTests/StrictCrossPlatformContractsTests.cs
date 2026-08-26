namespace Deep.Client.Maui.UiTests;

public sealed class StrictCrossPlatformContractsTests
{
    [Fact]
    public void Bounded_tool_output_is_decoded_as_strict_utf8()
    {
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");

        var result = StrictCrossPlatformContracts.RunBounded(
            powershell,
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
             "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);[Console]::Write('✓ Отправлено')"],
            TimeSpan.FromSeconds(15));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("✓ Отправлено", result.Output);
        Assert.Equal(string.Empty, result.Error);
    }

    [Fact]
    public void Correlated_message_descendant_requires_one_exact_ancestor_and_target()
    {
        const string xml = "<hierarchy><node resource-id='pkg:id/bubble' bounds='[0,0][20,20]'><node resource-id='pkg:id/body' text='exact' bounds='[1,1][5,5]'/><node resource-id='pkg:id/status' text='✓' content-desc='Отправлено' bounds='[6,6][9,9]'/></node><node resource-id='pkg:id/bubble' bounds='[20,0][40,20]'><node resource-id='pkg:id/body' text='other' bounds='[21,1][25,5]'/></node></hierarchy>";

        var target = StrictCrossPlatformContracts.FindExactlyOneCorrelatedDescendant(
            xml, "pkg:id/bubble", "pkg:id/body", "exact", "pkg:id/status");

        Assert.Equal("✓", target.AccessibleText);
        Assert.True(target.HasExactPresentation("✓", "Отправлено"));
        Assert.Equal("pkg:id/bubble",
            StrictCrossPlatformContracts.FindExactlyOneResourceIdContainingDescendantText(
                xml, "pkg:id/bubble", "pkg:id/body", "exact").ResourceId);
    }

    [Fact]
    public void Last_correlated_message_descendant_selects_the_newest_duplicate()
    {
        const string xml = "<hierarchy><node resource-id='pkg:id/bubble' bounds='[0,0][20,20]'><node resource-id='pkg:id/file' text='same.bin' bounds='[1,1][5,5]'/><node resource-id='pkg:id/action' text='old' bounds='[6,6][9,9]'/></node><node resource-id='pkg:id/bubble' bounds='[20,0][40,20]'><node resource-id='pkg:id/file' text='same.bin' bounds='[21,1][25,5]'/><node resource-id='pkg:id/action' text='new' bounds='[26,6][29,9]'/></node></hierarchy>";

        Assert.Throws<InvalidOperationException>(() =>
            StrictCrossPlatformContracts.FindExactlyOneCorrelatedDescendant(
                xml, "pkg:id/bubble", "pkg:id/file", "same.bin", "pkg:id/action"));
        var target = StrictCrossPlatformContracts.FindLastCorrelatedDescendant(
            xml, "pkg:id/bubble", "pkg:id/file", "same.bin", "pkg:id/action");

        Assert.Equal("new", target.AccessibleText);
    }

    [Theory]
    [InlineData("…", "Отправка")]
    [InlineData("!", "Ошибка отправки")]
    [InlineData("✓", "Прочитано")]
    public void Android_delivery_status_rejects_non_sent_presentations(
        string glyph, string description)
    {
        var node = new StrictCrossPlatformContracts.AndroidNode(
            "pkg:id/status", glyph, description,
            new StrictCrossPlatformContracts.AndroidBounds(0, 0, 1, 1));

        Assert.False(node.HasExactPresentation("✓", "Отправлено"));
    }
    [Fact]
    public void Ui_dump_accepts_one_exact_hierarchy_and_only_the_platform_banner()
    {
        const string xml = "<?xml version='1.0'?><hierarchy><node /></hierarchy>";

        Assert.Equal(xml, StrictCrossPlatformContracts.ExtractExactUiHierarchy(
            xml + "UI hierchary dumped to: /dev/tty\r\n"));
        Assert.Equal(xml, StrictCrossPlatformContracts.ExtractExactUiHierarchy(
            "\r\n" + xml + "UI hierarchy dumped to: /dev/tty\r\n"));
        Assert.Throws<InvalidOperationException>(() =>
            StrictCrossPlatformContracts.ExtractExactUiHierarchy("noise" + xml));
        Assert.Throws<InvalidOperationException>(() =>
            StrictCrossPlatformContracts.ExtractExactUiHierarchy(xml + "untrusted"));
        Assert.Throws<InvalidOperationException>(() =>
            StrictCrossPlatformContracts.ExtractExactUiHierarchy(xml + xml));
    }

    [Fact]
    public void Resource_id_lookup_requires_one_exact_node_and_derives_its_center()
    {
        const string xml = "<hierarchy><node resource-id='network.xpoint.deep.e2e:id/Chat_Send' text='' content-desc='accessible value' bounds='[20,40][100,80]' /></hierarchy>";

        var node = StrictCrossPlatformContracts.FindExactlyOneResourceId(xml, "network.xpoint.deep.e2e:id/Chat_Send");

        Assert.Equal((60, 60), node.Bounds.Center);
        Assert.Equal("accessible value", node.AccessibleText);
    }

    [Fact]
    public void Resource_id_lookup_rejects_missing_or_ambiguous_ids()
    {
        const string empty = "<hierarchy />";
        const string duplicate = "<hierarchy><node resource-id='pkg:id/a' bounds='[0,0][1,1]' /><node resource-id='pkg:id/a' bounds='[1,1][2,2]' /></hierarchy>";

        Assert.Throws<InvalidOperationException>(() => StrictCrossPlatformContracts.FindExactlyOneResourceId(empty, "pkg:id/a"));
        Assert.Throws<InvalidOperationException>(() => StrictCrossPlatformContracts.FindExactlyOneResourceId(duplicate, "pkg:id/a"));
        Assert.Equal(2, StrictCrossPlatformContracts.FindAllResourceIds(
            duplicate, "pkg:id/a").Length);
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
    public void Exact_accessible_text_count_never_accepts_a_substring()
    {
        const string xml = "<hierarchy><node resource-id='pkg:id/body' text='exact' bounds='[0,0][1,1]' /><node resource-id='pkg:id/body' text='exact suffix' bounds='[1,1][2,2]' /></hierarchy>";

        Assert.Equal(1, StrictCrossPlatformContracts.CountResourceIdsWithAccessibleText(
            xml, "pkg:id/body", "exact"));
        Assert.Equal(0, StrictCrossPlatformContracts.CountResourceIdsWithAccessibleText(
            xml, "pkg:id/body", "suffix"));
    }

    [Fact]
    public void Image_metadata_requires_and_round_trips_the_full_canonical_structure()
    {
        var parsed = StrictCrossPlatformContracts.ParseCanonicalImageMetadata(
            "fixture.jpg; image/jpeg; 631; 1x1");

        Assert.Equal("fixture.jpg", parsed.FileName);
        Assert.Equal("image/jpeg", parsed.MimeType);
        Assert.Equal(631, parsed.SizeBytes);
        Assert.Equal(1, parsed.Width);
        Assert.Equal(1, parsed.Height);
        Assert.Equal("fixture.jpg; image/jpeg; 631; 1x1", parsed.ToString());
        Assert.Throws<InvalidOperationException>(() =>
            StrictCrossPlatformContracts.ParseCanonicalImageMetadata(
                "fixture.jpg; image/jpeg; 0631; 1x1"));
        Assert.Throws<InvalidOperationException>(() =>
            StrictCrossPlatformContracts.ParseCanonicalImageMetadata(
                "fixture.jpg image/jpeg 631 1x1"));
    }

    [Fact]
    public void Last_item_descendant_correlation_survives_collection_virtualization()
    {
        const string bubble = "network.xpoint.deep.e2e:id/Chat.MessageBubble";
        const string play = "network.xpoint.deep.e2e:id/Chat.VoicePlayButton";
        var xml = $"<hierarchy><node resource-id='{bubble}' text='' content-desc='' bounds='[0,0][10,10]'><node resource-id='{play}' text='' content-desc='' bounds='[0,0][1,1]'/></node><node resource-id='{bubble}' text='' content-desc='' bounds='[0,10][10,20]'><node resource-id='network.xpoint.deep.e2e:id/Chat.MessageBody' text='anchor' content-desc='' bounds='[0,10][1,11]'/></node></hierarchy>";

        Assert.Throws<InvalidOperationException>(() =>
            StrictCrossPlatformContracts.FindLastResourceIdContainingDescendant(
                xml, bubble, play));

        var appended = xml.Replace(
            "</hierarchy>",
            $"<node resource-id='{bubble}' text='' content-desc='outgoing' bounds='[0,20][10,30]'><node resource-id='{play}' text='' content-desc='' bounds='[0,20][1,21]'/></node></hierarchy>",
            StringComparison.Ordinal);
        var correlated = StrictCrossPlatformContracts.FindLastResourceIdContainingDescendant(
            appended, bubble, play);

        Assert.Equal("outgoing", correlated.ContentDescription);
        Assert.Equal(3, StrictCrossPlatformContracts.FindAllResourceIds(appended, bubble).Length);
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
    public void Physical_inventory_requires_android_9_or_newer()
    {
        Assert.Throws<InvalidOperationException>(() =>
            StrictCrossPlatformContracts.RequirePhysicalDeviceInventory(
                "vendor/device/release", "Physical Device", "physical", "hardware", "nosdcard", 27));

        StrictCrossPlatformContracts.RequirePhysicalDeviceInventory(
            "vendor/device/release", "Physical Device", "physical", "hardware", "nosdcard", 28);
    }

    [Fact]
    public void Windows_output_tree_pin_covers_adjacent_runtime_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "deep-output-tree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "Deep.Client.Maui.exe"), "apphost");
            File.WriteAllText(Path.Combine(root, "Deep.Client.Shared.dll"), "shared-v1");
            var pinned = StrictCrossPlatformContracts.Sha256Tree(root);
            var expectedTranscript = string.Join('\n',
                $"Deep.Client.Maui.exe\t7\t{StrictCrossPlatformContracts.Sha256File(Path.Combine(root, "Deep.Client.Maui.exe"))}",
                $"Deep.Client.Shared.dll\t9\t{StrictCrossPlatformContracts.Sha256File(Path.Combine(root, "Deep.Client.Shared.dll"))}") + "\n";
            var expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(expectedTranscript)));

            Assert.Equal(expected, pinned);
            StrictCrossPlatformContracts.RequirePinnedTree(root, pinned, "Windows output tree");
            File.WriteAllText(Path.Combine(root, "Deep.Client.Shared.dll"), "shared-v2");

            Assert.Throws<InvalidOperationException>(() =>
                StrictCrossPlatformContracts.RequirePinnedTree(root, pinned, "Windows output tree"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
    public async Task Downloads_snapshot_waits_until_the_save_writer_closes_a_nonempty_file()
    {
        var root = Path.Combine(Path.GetTempPath(), $"deep-download-writer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var saved = Path.Combine(root, "fixture.bin");
        try
        {
            var snapshot = StrictCrossPlatformContracts.SnapshotDownloads(root);
            using (var writer = new FileStream(
                saved, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                writer.Write([0x11, 0x22, 0x33]);
                writer.Flush();
                var wait = Task.Run(() => snapshot.WaitForNewCorrelatedFile(
                    "fixture.bin", TimeSpan.FromSeconds(3)));
                await Task.Delay(300);
                Assert.False(wait.IsCompleted);
                writer.Dispose();
                Assert.Equal(saved, await wait);
            }
        }
        finally
        {
            if (File.Exists(saved)) File.Delete(saved);
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

    [Fact]
    public void Privacy_route_proof_requires_exact_three_disjoint_hops_and_selected_entry()
    {
        var ids = Enumerable.Range(1, 6)
            .Select(value => value.ToString("x2") + new string('a', 62))
            .ToArray();
        var canonical = $"v1|path=/api/ingress/v1/frame" +
            $"|primary={string.Join(',', ids[..3])}" +
            $"|fallback={string.Join(',', ids[3..])}" +
            $"|selected=fallback|entry={ids[3]}";

        var proof = StrictCrossPlatformContracts.PrivacyRouteProof.ParseExact(canonical);

        Assert.Equal("fallback", proof.Selected);
        Assert.Equal(ids[3], proof.Entry);
        Assert.Equal(3, proof.Primary.Count);
        Assert.Equal(3, proof.Fallback.Count);
        Assert.Throws<InvalidOperationException>(() =>
            StrictCrossPlatformContracts.PrivacyRouteProof.ParseExact(
                canonical.Replace(ids[5], ids[0], StringComparison.Ordinal)));
        Assert.Throws<InvalidOperationException>(() =>
            StrictCrossPlatformContracts.PrivacyRouteProof.ParseExact(
                canonical.Replace($"entry={ids[3]}", $"entry={ids[4]}",
                    StringComparison.Ordinal)));
    }
}
