using System.Text.Json;
using HistoryJanus.Git;
using Xunit;

namespace HistoryJanus.Contracts;

/// <summary>
/// 跨进程 JSON 投影合同：页面消费宿主总线时，结构化结果先从 JSON 投影为页面模型
/// (REQ-002)。这些 record 是投影边界，序列化形状的意外变更必须在 Contracts 阶段失败。
/// </summary>
public sealed class JsonProjectionContractTests
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void BranchHistoryReportSurvivesJsonRoundTrip()
    {
        var source = new BranchHistoryReport(
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
                    "fedcba9876543210", "fedcba9876", "author",
                    new DateTimeOffset(2026, 8, 8, 13, 0, 0, TimeSpan.Zero),
                    "head commit", 1, false, true, CommitRemoteState.LocalOnly),
            ]);

        var restored = RoundTrip(source);

        Assert.Equal("2026-020-HistoryJanus", restored.Branch);
        Assert.Equal(BranchRemoteState.InSync, restored.RemoteState);
        Assert.Equal(2, restored.Entries.Count);
        Assert.True(restored.Entries[0].IsForkPoint);
        Assert.False(restored.Entries[0].IsMerge);
        Assert.True(restored.Entries[1].IsHead);
        Assert.Equal(CommitRemoteState.LocalOnly, restored.Entries[1].RemoteState);
    }

    [Fact]
    public void CommitDetailSurvivesJsonRoundTripWithRename()
    {
        var source = new CommitDetail(
            Branch: "main",
            Sha: "fedcba9876543210",
            ShortSha: "fedcba9876",
            Author: "author",
            AuthorEmail: "a@example.com",
            CommittedAt: new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero),
            Subject: "subject",
            Body: "body",
            Parents: ["0123456789abcdef"],
            Files:
            [
                new CommitFileChange("A", "src/New.cs"),
                new CommitFileChange("R", "src/Renamed.cs", "src/Old.cs"),
            ]);

        var restored = RoundTrip(source);

        Assert.Equal("subject", restored.Subject);
        Assert.Equal("0123456789abcdef", Assert.Single(restored.Parents));
        Assert.Equal(2, restored.Files.Count);
        Assert.Equal("src/Old.cs -> src/Renamed.cs", restored.Files[1].DisplayPath);
    }

    [Fact]
    public void BranchDiffReportSurvivesJsonRoundTrip()
    {
        var source = new BranchDiffReport(
            Branch: "main",
            TargetSha: "0123456789abcdef",
            HeadSha: "fedcba9876543210",
            CommitCount: 1,
            FileCount: 1,
            ShortStat: " 1 file changed",
            DiffText: "diff --git a/x b/x",
            DiffTruncated: false,
            Files: [new CommitFileChange("M", "x")]);

        var restored = RoundTrip(source);

        Assert.Equal(1, restored.CommitCount);
        Assert.Equal("diff --git a/x b/x", restored.DiffText);
        Assert.False(restored.DiffTruncated);
    }

    [Fact]
    public void BranchMutationReportSurvivesJsonRoundTrip()
    {
        var source = new BranchMutationReport(
            Success: true,
            Changed: true,
            Branch: "main",
            TargetSha: "0123456789abcdef",
            BeforeSha: "fedcba9876543210",
            AfterSha: "0123456789abcdef",
            Message: "rolled back");

        var restored = RoundTrip(source);

        Assert.True(restored.Success);
        Assert.True(restored.Changed);
        Assert.Equal("fedcba9876543210", restored.BeforeSha);
    }

    private static T RoundTrip<T>(T source)
    {
        var json = JsonSerializer.Serialize(source);
        return Assert.IsType<T>(JsonSerializer.Deserialize<T>(json, Options));
    }
}
