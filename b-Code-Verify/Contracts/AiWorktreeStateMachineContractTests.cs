using System.Text.Json;
using HistoryJanus.Git;
using Xunit;

namespace HistoryJanus.Contracts;

/// <summary>
/// AI 工作树状态机合同：合法转移、非法跳步、HEAD 失效与分支命名在 Contracts 阶段冻结。
/// </summary>
public sealed class AiWorktreeStateMachineContractTests
{
    private const string Head = "0123456789abcdef0123456789abcdef01234567";
    private const string Other = "fedcba9876543210fedcba9876543210fedcba98";

    [Fact]
    public void LegalHappyPathTransitionsSucceed()
    {
        AssertTransition(AiWorktreeStatus.Active, AiWorktreeStatus.Ready, Head, null, true);
        AssertTransition(AiWorktreeStatus.Ready, AiWorktreeStatus.Verified, Head, Head, true);
        AssertTransition(AiWorktreeStatus.Verified, AiWorktreeStatus.Approved, Head, Head, true);
        AssertTransition(AiWorktreeStatus.Approved, AiWorktreeStatus.Merged, Head, Head, true);
        AssertTransition(AiWorktreeStatus.Merged, AiWorktreeStatus.Cleaned, Head, Head, true);
    }

    [Fact]
    public void IllegalSkipsAreRejected()
    {
        AssertTransition(AiWorktreeStatus.Active, AiWorktreeStatus.Merged, Head, Head, false);
        AssertTransition(AiWorktreeStatus.Active, AiWorktreeStatus.Verified, Head, Head, false);
        AssertTransition(AiWorktreeStatus.Ready, AiWorktreeStatus.Approved, Head, Head, false);
        AssertTransition(AiWorktreeStatus.Verified, AiWorktreeStatus.Merged, Head, Head, false);
        AssertTransition(AiWorktreeStatus.Verified, AiWorktreeStatus.Cleaned, Head, Head, false);
    }

    [Fact]
    public void HeadChangeInvalidatesVerifiedAndApproved()
    {
        Assert.False(AiWorktreeStateMachine.TryTransition(
            AiWorktreeStatus.Ready, AiWorktreeStatus.Verified, Other, Head, out var error));
        Assert.Contains("失效", error);

        Assert.False(AiWorktreeStateMachine.TryTransition(
            AiWorktreeStatus.Verified, AiWorktreeStatus.Approved, Other, Head, out error));
        Assert.Contains("失效", error);

        Assert.False(AiWorktreeStateMachine.TryTransition(
            AiWorktreeStatus.Approved, AiWorktreeStatus.Merged, Other, Head, out error));
        Assert.Contains("失效", error);
    }

    [Fact]
    public void CleanupFailedRetainsFailureAndDoesNotReportCleaned()
    {
        Assert.True(AiWorktreeStateMachine.TryTransition(
            AiWorktreeStatus.Merged, AiWorktreeStatus.CleanupFailed, Head, Head, out var error), error);
        Assert.Equal("cleanupFailed", AiWorktreeStateMachine.FormatStatus(AiWorktreeStatus.CleanupFailed));
        Assert.NotEqual("cleaned", AiWorktreeStateMachine.FormatStatus(AiWorktreeStatus.CleanupFailed));

        Assert.False(AiWorktreeStateMachine.TryTransition(
            AiWorktreeStatus.CleanupFailed, AiWorktreeStatus.Cleaned, Head, Head, out error));
        Assert.Contains("不允许", error);
        Assert.DoesNotContain("已清空", error);

        Assert.True(AiWorktreeStateMachine.TryTransition(
            AiWorktreeStatus.Abandoned, AiWorktreeStatus.CleanupFailed, Head, null, out error), error);
        Assert.False(AiWorktreeStateMachine.TryTransition(
            AiWorktreeStatus.CleanupFailed, AiWorktreeStatus.Cleaned, Head, null, out error));
        Assert.Contains("不允许从 cleanupFailed 转移到", error);
    }

    [Fact]
    public void FailedAndAbandonedAreLegalFromActiveAndReady()
    {
        AssertTransition(AiWorktreeStatus.Active, AiWorktreeStatus.Failed, Head, null, true);
        AssertTransition(AiWorktreeStatus.Active, AiWorktreeStatus.Abandoned, Head, null, true);
        AssertTransition(AiWorktreeStatus.Ready, AiWorktreeStatus.Failed, Other, Head, true);
        AssertTransition(AiWorktreeStatus.Ready, AiWorktreeStatus.Abandoned, Other, Head, true);
        AssertTransition(AiWorktreeStatus.Verified, AiWorktreeStatus.Failed, Head, Head, true);
        AssertTransition(AiWorktreeStatus.Approved, AiWorktreeStatus.Failed, Head, Head, true);
    }

    [Fact]
    public void ReadyVerifiedApprovedMergedRequireFullHeadSha()
    {
        Assert.False(AiWorktreeStateMachine.TryTransition(
            AiWorktreeStatus.Active, AiWorktreeStatus.Ready, "abc1234", null, out var error));
        Assert.Contains("完整 HEAD SHA", error);

        Assert.False(AiWorktreeStateMachine.TryTransition(
            AiWorktreeStatus.Ready, AiWorktreeStatus.Verified, Head, "abc1234", out error));
        Assert.Contains("完整 HEAD SHA", error);
    }

    [Theory]
    [InlineData("ai/2026-021-HistoryMercury/a1b2c3d-1-fix-dock-layout", true)]
    [InlineData("ai/2026-020-HistoryJanus/0123456789-2-slug", true)]
    [InlineData("ai/2026-025-新项目/deadbee-1-x", true)]
    [InlineData("ai/proj/abc-1-slug", false)]
    [InlineData("feature/foo", false)]
    [InlineData("ai/proj/a1b2c3d-1-Fix", false)]
    [InlineData("ai/proj/a1b2c3d-1-fix_dock", false)]
    [InlineData("ai/proj/a1b2c3d-0-slug", false)]
    [InlineData("ai/a/b/c", false)]
    [InlineData("ai/proj/a1b2c3d-1-", false)]
    [InlineData("", false)]
    public void BranchNamingAcceptsAndRejects(string branchName, bool accepted)
    {
        var ok = AiWorktreeStateMachine.TryParseBranchName(branchName, out var parsed, out var error);
        Assert.Equal(accepted, ok);
        if (accepted)
        {
            Assert.NotNull(parsed);
            Assert.DoesNotContain("/", parsed.FolderName);
            Assert.DoesNotContain("\\", parsed.FolderName);
            Assert.Equal(parsed.FolderName, $"{parsed.ShortSha}-{parsed.Sequence}-{parsed.GoalSlug}");
        }
        else
        {
            Assert.Null(parsed);
            Assert.False(string.IsNullOrWhiteSpace(error));
        }
    }

    [Fact]
    public void ParsedExampleMatchesFolderNameContract()
    {
        Assert.True(AiWorktreeStateMachine.TryParseBranchName(
            "ai/2026-021-HistoryMercury/a1b2c3d-1-fix-dock-layout", out var parsed, out var error), error);
        Assert.Equal("2026-021-HistoryMercury", parsed!.ProjectName);
        Assert.Equal("a1b2c3d", parsed.ShortSha);
        Assert.Equal(1, parsed.Sequence);
        Assert.Equal("fix-dock-layout", parsed.GoalSlug);
        Assert.Equal("a1b2c3d-1-fix-dock-layout", parsed.FolderName);

        Assert.True(AiWorktreeStateMachine.TryFormatWorktreeFolderName(
            "a1b2c3d", 1, "fix-dock-layout", out var folder, out error), error);
        Assert.Equal("a1b2c3d-1-fix-dock-layout", folder);
        Assert.True(AiWorktreeStateMachine.TryValidateSlug("fix-dock-layout", out error), error);
        Assert.False(AiWorktreeStateMachine.TryValidateSlug("Fix", out _));
        Assert.False(AiWorktreeStateMachine.TryFormatWorktreeFolderName("a1b2c3d", 0, "slug", out _, out _));
    }

    [Fact]
    public void RecordSurvivesCamelCaseJsonRoundTrip()
    {
        var source = new AiWorktreeRecord
        {
            TaskId = "task-1",
            ProjectName = "2026-020-HistoryJanus",
            BaselineSha = Head,
            BranchName = "ai/2026-020-HistoryJanus/0123456-1-demo",
            WorktreePath = @"F:\ai工作区\2026-020-HistoryJanus\0123456-1-demo",
            GoalSlug = "demo",
            HeadSha = Head,
            BoundHeadSha = Head,
            Status = AiWorktreeStatus.Ready,
            DianaReleaseId = "",
            Evidence = "",
            Approver = "",
            CreatedAt = new DateTimeOffset(2026, 8, 13, 1, 0, 0, TimeSpan.Zero),
        };

        var json = JsonSerializer.Serialize(source, AiWorktreeJson.Options);
        Assert.Contains("\"taskId\":", json);
        Assert.Contains("\"cleanupFailed\"", JsonSerializer.Serialize(
            AiWorktreeStatus.CleanupFailed, AiWorktreeJson.Options));
        Assert.DoesNotContain("isExpanded", json, StringComparison.OrdinalIgnoreCase);

        var restored = Assert.IsType<AiWorktreeRecord>(
            JsonSerializer.Deserialize<AiWorktreeRecord>(json, AiWorktreeJson.Options));
        Assert.Equal(source.TaskId, restored.TaskId);
        Assert.Equal(AiWorktreeStatus.Ready, restored.Status);
        Assert.Equal(Head, restored.BoundHeadSha);
    }

    private static void AssertTransition(
        AiWorktreeStatus current,
        AiWorktreeStatus next,
        string currentHeadSha,
        string? boundHeadSha,
        bool expected)
    {
        var ok = AiWorktreeStateMachine.TryTransition(
            current, next, currentHeadSha, boundHeadSha, out var error);
        Assert.Equal(expected, ok);
        if (expected)
            Assert.True(string.IsNullOrEmpty(error));
        else
            Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
