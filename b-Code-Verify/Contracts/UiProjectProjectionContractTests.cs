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
}
