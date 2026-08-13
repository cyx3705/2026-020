namespace HistoryJanus.Git;

/// <summary>图谱分支分类：编号主线、仍可能出现的继承标记、平行（含 ai/）工作分支。</summary>
public enum BranchKind
{
    Mainline,
    Inheritance,
    AiWork,
    Parallel,
}

/// <summary>本地或远端 ref；不含布局坐标。</summary>
public sealed record GraphRef
{
    public required string Name { get; init; }

    public string FullName { get; init; } = "";

    public bool IsRemote { get; init; }

    public string TargetSha { get; init; } = "";

    public BranchKind Kind { get; init; }

    /// <summary>未合并的平行分支为 true；已合并仍保留历史行，但右侧不再画端点。</summary>
    public bool IsOpen { get; init; } = true;
}

/// <summary>
/// 提交节点。列表页 FileSummary / ReleaseId / ReleaseStatus 可为空；
/// 证据槽留给后续 Diana 版本，Phase 1 保持空字符串。
/// </summary>
public sealed record GraphCommitNode
{
    public required string Sha { get; init; }

    public string ShortSha { get; init; } = "";

    public List<string> Parents { get; init; } = [];

    public string Author { get; init; } = "";

    public DateTimeOffset CommittedAt { get; init; }

    public string Subject { get; init; } = "";

    public string FileSummary { get; init; } = "";

    public List<string> BranchRefs { get; init; } = [];

    public string ReleaseId { get; init; } = "";

    public string ReleaseStatus { get; init; } = "";
}

/// <summary>
/// 提交 DAG 边：FromSha 为父提交，ToSha 为子提交。
/// IsMergeParent 为 true 表示该父提交不是子提交的第一父。
/// </summary>
public sealed record GraphEdge
{
    public required string FromSha { get; init; }

    public required string ToSha { get; init; }

    public bool IsMergeParent { get; init; }
}

/// <summary>janus.graph.commits 分页游标。</summary>
public sealed record GraphPage
{
    public int Skip { get; init; }

    public int Limit { get; init; } = 200;

    public bool HasMore { get; init; }

    public string? UntilSha { get; init; }
}

/// <summary>分支与其父/基线关系，供 janus.graph.branches 使用。</summary>
public sealed record GraphBranchRelation
{
    public required string BranchName { get; init; }

    public BranchKind Kind { get; init; }

    public string ParentBranch { get; init; } = "";

    public string BaselineSha { get; init; } = "";
}

/// <summary>janus.graph.summary 返回合同：项目、分支、HEAD、节点数与 dirty。</summary>
public sealed record GraphSummary
{
    public required string ProjectName { get; init; }

    public string HeadSha { get; init; } = "";

    public string HeadShortSha { get; init; } = "";

    public int NodeCount { get; init; }

    public int EdgeCount { get; init; }

    public int BranchCount { get; init; }

    public bool IsDirty { get; init; }

    public string DirtyMessage { get; init; } = "";

    public List<GraphRef> Branches { get; init; } = [];
}

/// <summary>janus.graph.branches 返回合同：编号主线 + 平行分支（含已合并历史）。</summary>
public sealed record GraphBranchesReport
{
    public required string ProjectName { get; init; }

    public GraphRef? Mainline { get; init; }

    public List<GraphRef> Inheritance { get; init; } = [];

    public List<GraphRef> AiWork { get; init; } = [];

    public List<GraphRef> Parallels { get; init; } = [];

    public List<GraphBranchRelation> Relations { get; init; } = [];
}

/// <summary>janus.graph.commits 返回合同。</summary>
public sealed record GraphCommitsReport
{
    public required string ProjectName { get; init; }

    public string Name { get; init; } = "";

    public GraphPage Page { get; init; } = new();

    public List<GraphCommitNode> Nodes { get; init; } = [];

    public List<GraphEdge> Edges { get; init; } = [];

    /// <summary>第 0 条为主线，其后为平行分支；<see cref="GraphRef.IsOpen"/> 决定右侧是否画 tip。</summary>
    public List<GraphRef> Lanes { get; init; } = [];
}

/// <summary>janus.graph.node 返回合同：单提交、父边、差异摘要与空证据槽。</summary>
public sealed record GraphNodeDetail
{
    public required GraphCommitNode Node { get; init; }

    public List<GraphEdge> ParentEdges { get; init; } = [];

    public string FileSummary { get; init; } = "";

    public string DiffSummary { get; init; } = "";

    public string ReleaseId { get; init; } = "";

    public string ReleaseStatus { get; init; } = "";

    public string Evidence { get; init; } = "";
}
