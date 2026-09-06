using HistoryJanus.Git;
using HistoryJanus.Module;
using Xunit;

namespace HistoryJanus.Contracts;

/// <summary>
/// V5.6 分支历史子页：HEAD 在最上面，列表只留时间、标记、远端、说明四列。
/// </summary>
public sealed class BranchHistoryRowContractTests
{
    private static BranchHistoryReport Report() => new(
        Branch: "2026-020-HistoryJanus",
        ParentBranch: "main",
        ForkSha: "0123456789abcdef",
        HeadSha: "fedcba9876543210",
        RemoteHeadSha: "fedcba9876543210",
        RemoteState: BranchRemoteState.InSync,
        AheadCount: 0,
        BehindCount: 0,
        TotalOwnCommits: 2,
        Skip: 0,
        Limit: 200,
        HasMore: false,
        RemoteRefreshFailed: false,
        RemoteMessage: null,
        Entries:
        [
            new BranchHistoryEntry(
                "0123456789abcdef", "0123456789", "author",
                new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero),
                "fork point", 1, true, false, CommitRemoteState.Pushed),
            new BranchHistoryEntry(
                "aaaaaaaaaaaaaaaa", "aaaaaaaaaa", "author",
                new DateTimeOffset(2026, 8, 8, 12, 30, 0, TimeSpan.Zero),
                "middle commit", 1, false, false, CommitRemoteState.Pushed),
            new BranchHistoryEntry(
                "fedcba9876543210", "fedcba9876", "author",
                new DateTimeOffset(2026, 8, 8, 13, 0, 0, TimeSpan.Zero),
                "head commit", 1, false, true, CommitRemoteState.LocalOnly),
        ]);

    [Fact]
    public void HeadIsTheFirstRowAndTheForkPointIsLast()
    {
        var rows = HistoryJanusUiCommands.UiHistoryProjection.Read(Report());

        Assert.Equal(
            ["head commit", "middle commit", "fork point"],
            rows.Select(row => row["subject"]));
        Assert.Equal("HEAD", rows[0]["marker"]);
        Assert.Equal("分叉点", rows[^1]["marker"]);
    }

    /// <summary>
    /// 短 sha 与作者不再有列，但仍留在行里：行数据是选中通道的载荷，
    /// 别处按字段名取值，从投影里删掉会连带断掉。
    /// </summary>
    [Fact]
    public void RowsStillCarryShaAndAuthorEvenThoughTheColumnsAreGone()
    {
        var row = HistoryJanusUiCommands.UiHistoryProjection.Read(Report())[0];

        Assert.Equal("fedcba9876", row["sha"]);
        Assert.Equal("author", row["author"]);
        Assert.Equal("仅本地", row["remote"]);
    }

    [Fact]
    public void NonReportPayloadYieldsNoRows()
        => Assert.Empty(HistoryJanusUiCommands.UiHistoryProjection.Read("not a report"));
}
