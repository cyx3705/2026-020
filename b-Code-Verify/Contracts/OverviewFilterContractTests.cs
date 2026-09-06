using HistoryJanus.Git;
using HistoryJanus.Module;
using Xunit;

namespace HistoryJanus.Contracts;

/// <summary>
/// V5.6 总览页顶栏：搜索、年份与行序。三条都落在投影层，业务命令的输出次序不动。
/// </summary>
public sealed class OverviewFilterContractTests
{
    private const string AllYears = "全部";

    private static readonly WorktreeInfo[] Projects =
    [
        new("2025-003-光伏货架车", "C:\\a", ZFolderCount: 1, ZFolders: ["z-Publish"]),
        new("2026-020-HistoryJanus", "C:\\b", ZFolderCount: 1, ZFolders: ["z-Publish"]),
        new("2026-014-CsharpLearn", "C:\\c", ZFolderCount: 1, ZFolders: ["z-课件归档"]),
    ];

    [Fact]
    public void NewestProjectComesFirst()
    {
        var ordered = HistoryJanusUiCommands.UiProjectProjection.Newest(Projects);

        Assert.Equal(
            ["2026-020-HistoryJanus", "2026-014-CsharpLearn", "2025-003-光伏货架车"],
            ordered.Select(project => project.BranchName));
    }

    [Fact]
    public void SearchMatchesProjectNameAndZFolderName()
    {
        var byName = HistoryJanusUiCommands.UiProjectProjection.Filter(
            Projects, "janus", null, AllYears);
        Assert.Equal(["2026-020-HistoryJanus"], byName.Select(project => project.BranchName));

        // z 级文件夹也在搜索范围里：项目名里没有「课件」，只有那个 z 目录有。
        var byZFolder = HistoryJanusUiCommands.UiProjectProjection.Filter(
            Projects, "课件", null, AllYears);
        Assert.Equal(["2026-014-CsharpLearn"], byZFolder.Select(project => project.BranchName));
    }

    [Fact]
    public void YearFilterKeepsOnlyThatYearAndAllYearsIsAPassThrough()
    {
        var filtered = HistoryJanusUiCommands.UiProjectProjection.Filter(
            Projects, null, "2026", AllYears);
        Assert.Equal(
            ["2026-020-HistoryJanus", "2026-014-CsharpLearn"],
            filtered.Select(project => project.BranchName));

        Assert.Equal(3, HistoryJanusUiCommands.UiProjectProjection
            .Filter(Projects, null, AllYears, AllYears).Count);
        Assert.Equal(3, HistoryJanusUiCommands.UiProjectProjection
            .Filter(Projects, "", "", AllYears).Count);
    }

    [Fact]
    public void SearchAndYearIntersect()
    {
        var filtered = HistoryJanusUiCommands.UiProjectProjection.Filter(
            Projects, "z-Publish", "2026", AllYears);

        Assert.Equal(["2026-020-HistoryJanus"], filtered.Select(project => project.BranchName));
    }

    [Fact]
    public void YearOptionsLeadWithAllYearsThenNewestFirst()
    {
        var options = HistoryJanusUiCommands.UiProjectProjection.YearOptions(Projects, AllYears);

        // 候选行只认 value 这一列（Aurora 的 optionsSource 合同）。
        Assert.Equal([AllYears, "2026", "2025"], options.Select(row => row["value"]));
    }

    [Fact]
    public void ProjectsWithoutAYearPrefixSurviveAndOfferNoYearOption()
    {
        WorktreeInfo[] projects = [new("0000-001-AIReady", "C:\\t"), new("scratch", "C:\\s")];

        Assert.Equal(
            [AllYears, "0000"],
            HistoryJanusUiCommands.UiProjectProjection.YearOptions(projects, AllYears)
                .Select(row => row["value"]));
        Assert.Equal(2, HistoryJanusUiCommands.UiProjectProjection
            .Filter(projects, null, AllYears, AllYears).Count);
    }
}
