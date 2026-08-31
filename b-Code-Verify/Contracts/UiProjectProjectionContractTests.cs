using System.Text.Json;
using HistoryJanus.Git;
using HistoryJanus.Module;
using Xunit;

namespace HistoryJanus.Contracts;

public sealed class UiProjectProjectionContractTests
{
    [Fact]
    public void ProjectsWorktreeFieldsIntoTheDescriptivePageRowShape()
    {
        var payload = HistoryJanusUiCommands.UiProjectProjection.Serialize(new WorktreeInfo[]
        {
            new WorktreeInfo("clean", "C:\\clean", LastCommitMessage: "first", IsClean: true),
            new WorktreeInfo("dirty", "C:\\dirty", LastCommitMessage: "second", IsClean: false),
            new WorktreeInfo("unknown", "C:\\unknown", IsClean: null),
        });

        using var document = JsonDocument.Parse(payload);
        var rows = document.RootElement.EnumerateArray().ToArray();

        Assert.Equal(3, rows.Length);
        Assert.Equal("clean", rows[0].GetProperty("name").GetString());
        Assert.Equal("干净", rows[0].GetProperty("isClean").GetString());
        Assert.Equal("first", rows[0].GetProperty("subject").GetString());
        Assert.Equal("有修改", rows[1].GetProperty("isClean").GetString());
        Assert.Equal("未知", rows[2].GetProperty("isClean").GetString());
        Assert.False(rows[0].TryGetProperty("BranchName", out _));
        Assert.False(rows[0].TryGetProperty("IsClean", out _));
    }

    [Fact]
    public void ZFolderDisplayDistinguishesNoneOneAndMany()
    {
        var payload = HistoryJanusUiCommands.UiProjectProjection.Serialize(new WorktreeInfo[]
        {
            new("none", "C:\\none"),
            new("one", "C:\\one", ZFolderCount: 1, ZFolders: ["z-Publish"]),
            new("unnamed", "C:\\unnamed", ZFolderCount: 1),
            new("many", "C:\\many", ZFolderCount: 3, ZFolders: ["z-One", "Z-Two", "z-Three"]),
        });

        using var document = JsonDocument.Parse(payload);
        var rows = document.RootElement.EnumerateArray().ToArray();

        Assert.Equal("无目录", rows[0].GetProperty("zFolders").GetString());
        Assert.Equal("z-Publish", rows[1].GetProperty("zFolders").GetString());
        Assert.Equal("1 个", rows[2].GetProperty("zFolders").GetString());
        Assert.Equal("3 个", rows[3].GetProperty("zFolders").GetString());
    }

    [Theory]
    [InlineData("提交", "提交", "●")]
    [InlineData("推送", "推送", "↑")]
    [InlineData("同步", "同步", "↔")]
    [InlineData("归档", "归档", "□")]
    [InlineData("拉取", "拉取", "↓")]
    [InlineData("未知", "同步", "↔")]
    [InlineData("", "同步", "↔")]
    public void LifecycleActionProjectsToFixedStatusSymbol(
        string sourceAction,
        string expectedAction,
        string expectedSymbol)
    {
        var payload = HistoryJanusUiCommands.UiProjectProjection.Serialize(new[]
        {
            new WorktreeInfo("project", "C:\\project", LifecycleAction: sourceAction),
        });

        using var document = JsonDocument.Parse(payload);
        var row = document.RootElement[0];

        Assert.Equal(expectedAction, row.GetProperty("status").GetString());
        Assert.Equal(expectedAction, row.GetProperty("lifecycleAction").GetString());
        Assert.Equal(expectedSymbol, row.GetProperty("statusSymbol").GetString());
    }
}
