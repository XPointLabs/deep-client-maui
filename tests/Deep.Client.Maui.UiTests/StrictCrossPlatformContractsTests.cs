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
}
