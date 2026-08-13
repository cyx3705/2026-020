using System.Text.Json;
using System.Text.Json.Serialization;

namespace HistoryJanus.Git;

/// <summary>AI 工作树任务状态。JSON 线名见 <see cref="AiWorktreeStateMachine.FormatStatus"/>。</summary>
public enum AiWorktreeStatus
{
    Active,
    Ready,
    Verified,
    Approved,
    Merged,
    Cleaned,
    Failed,
    Abandoned,
    CleanupFailed,
}

/// <summary>已解析的 AI 分支名：<c>ai/&lt;项目&gt;/&lt;短SHA&gt;-&lt;序号&gt;-&lt;slug&gt;</c>。</summary>
public sealed record AiWorktreeBranchName(
    string ProjectName,
    string ShortSha,
    int Sequence,
    string GoalSlug)
{
    public string BranchName => $"ai/{ProjectName}/{ShortSha}-{Sequence}-{GoalSlug}";

    /// <summary>工作树目录名，不含斜杠。</summary>
    public string FolderName => $"{ShortSha}-{Sequence}-{GoalSlug}";
}

/// <summary>
/// AI 工作树注册记录。注册表文件本身属于 Phase 2；本类型冻结字段合同。
/// </summary>
public sealed record AiWorktreeRecord
{
    public required string TaskId { get; init; }

    public required string ProjectName { get; init; }

    public string BaselineSha { get; init; } = "";

    public string BranchName { get; init; } = "";

    public string WorktreePath { get; init; } = "";

    public string GoalSlug { get; init; } = "";

    public string HeadSha { get; init; } = "";

    /// <summary>在 ready/verified/approved 绑定的完整 HEAD；HEAD 变化后验证与审批失效。</summary>
    public string BoundHeadSha { get; init; } = "";

    public AiWorktreeStatus Status { get; init; }

    public string DianaReleaseId { get; init; } = "";

    public string Evidence { get; init; } = "";

    public string Approver { get; init; } = "";

    public DateTimeOffset? ApprovedAt { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ClosedAt { get; init; }
}

/// <summary>与 <see cref="HistoryRecorder"/> 相同的 camelCase JSON 选项，供后续注册表使用。</summary>
public static class AiWorktreeJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
