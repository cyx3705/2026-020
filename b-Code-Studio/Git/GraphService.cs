using System.Globalization;

namespace HistoryJanus.Git;

/// <summary>
/// 当前编号项目仓内的只读提交 DAG。
/// 主线是仓内 main；范围天然是本仓。
/// 平行分支（主要是 ai/P/）含未合并与已合并历史；已合并行不在右侧画 tip。
/// git log --parents --date-order，不用 --first-parent。
/// </summary>
public sealed partial class GraphService
{
    private const int DefaultLimit = 200;
    private const int MaxLimit = 2000;
    private const int MaxDiffSummaryChars = 20_000;
    private const string LogFormat = "%H%x1f%h%x1f%an%x1f%aI%x1f%s%x1f%P%x1e";

    private readonly ProjectService _projects;

    public GraphService(ProjectService projects)
    {
        _projects = projects;
    }

    public async Task<(bool Success, string Message, GraphSummary? Summary)> GetSummaryAsync(
        string name, int limit = DefaultLimit, CancellationToken cancellation = default)
    {
        var scope = await ResolveScopeAsync(name, cancellation);
        if (!scope.Success || scope.Scope == null)
            return (false, scope.Message, null);

        var graph = scope.Scope;
        limit = ClampLimit(limit);
        var dirtyTask = ReadProjectDirtyAsync(graph.ProjectName, cancellation);
        var pageTask = LoadCommitPageAsync(graph, limit, skip: 0, cancellation);
        await Task.WhenAll(dirtyTask, pageTask);

        var page = await pageTask;
        if (!page.Success)
            return (false, page.Message, null);
        var dirty = await dirtyTask;

        var summary = new GraphSummary
        {
            ProjectName = graph.ProjectName,
            HeadSha = graph.Mainline.TargetSha,
            HeadShortSha = Short(graph.Mainline.TargetSha),
            NodeCount = page.Nodes.Count,
            EdgeCount = page.Edges.Count,
            BranchCount = graph.AllRefs.Count,
            IsDirty = dirty.IsDirty,
            DirtyMessage = dirty.Message,
            Branches = [.. graph.AllRefs],
        };
        var dirtyText = dirty.IsDirty ? "工作树有未提交变更" : dirty.Message;
        return (true,
            $"{graph.ProjectName}：HEAD {summary.HeadShortSha}，{summary.NodeCount} 个节点，" +
            $"{summary.BranchCount} 条分支；{dirtyText}",
            summary);
    }

    public async Task<(bool Success, string Message, GraphBranchesReport? Report)> GetBranchesAsync(
        string name, CancellationToken cancellation = default)
    {
        var scope = await ResolveScopeAsync(name, cancellation);
        if (!scope.Success || scope.Scope == null)
            return (false, scope.Message, null);

        var graph = scope.Scope;
        var report = new GraphBranchesReport
        {
            ProjectName = graph.ProjectName,
            Mainline = graph.Mainline,
            Inheritance = [],
            AiWork = [.. graph.Parallels.Where(item => item.Kind == BranchKind.AiWork)],
            Parallels = [.. graph.Parallels],
            Relations = [.. graph.Relations],
        };
        var openCount = graph.Parallels.Count(item => item.IsOpen);
        return (true,
            $"{graph.ProjectName}：主线 1，平行 {graph.Parallels.Count}（未合并 {openCount}）",
            report);
    }

    /// <summary>
    /// 提交按 --date-order 新→旧排列；skip 从最新端跳过，与 GraphPage 一致。
    /// </summary>
    public async Task<(bool Success, string Message, GraphCommitsReport? Report)> GetCommitsAsync(
        string name, int limit = DefaultLimit, int skip = 0, CancellationToken cancellation = default)
    {
        var scope = await ResolveScopeAsync(name, cancellation);
        if (!scope.Success || scope.Scope == null)
            return (false, scope.Message, null);

        var graph = scope.Scope;
        limit = ClampLimit(limit);
        skip = Math.Max(0, skip);
        var page = await LoadCommitPageAsync(graph, limit, skip, cancellation);
        if (!page.Success)
            return (false, page.Message, null);

        var report = new GraphCommitsReport
        {
            ProjectName = graph.ProjectName,
            Name = graph.ProjectName,
            Page = page.Page,
            Nodes = page.Nodes,
            Edges = page.Edges,
            Lanes = graph.Lanes(),
        };
        return (true,
            $"{graph.ProjectName}：显示 {page.Nodes.Count} 个提交（skip={skip}，limit={limit}）",
            report);
    }

    public async Task<(bool Success, string Message, GraphNodeDetail? Detail)> GetNodeAsync(
        string name, string sha, CancellationToken cancellation = default)
    {
        var scope = await ResolveScopeAsync(name, cancellation);
        if (!scope.Success || scope.Scope == null)
            return (false, scope.Message, null);
        if (string.IsNullOrWhiteSpace(sha))
            return (false, "提交 SHA 不能为空", null);

        var graph = scope.Scope;
        var resolved = await ResolveCommitAsync(graph.RepoPath, sha.Trim(), cancellation);
        if (resolved == null)
            return (false, $"无法解析提交对象: {sha}", null);

        var inScope = await IsReachableFromTipsAsync(resolved, graph, cancellation);
        if (!inScope.Success)
            return (false, inScope.Message, null);
        if (!inScope.Reachable)
            return (false, $"提交不在项目 {graph.ProjectName} 的图谱范围内: {sha}", null);

        const string format = "%H%x1f%h%x1f%an%x1f%aI%x1f%s%x1f%P";
        var meta = await GitRunner.RunAsync(graph.RepoPath,
            ["show", "-s", $"--format={format}", resolved], cancellation: cancellation);
        if (!meta.Success)
            return (false, $"读取提交详情失败:\n{meta.Output}", null);
        var node = ParseShow(meta.Output);
        if (node == null)
            return (false, "Git 返回的提交详情格式无效", null);

        var files = await GitRunner.RunAsync(graph.RepoPath,
            ["diff-tree", "--root", "--no-commit-id", "--name-status", "-r", resolved],
            cancellation: cancellation);
        var stat = await GitRunner.RunAsync(graph.RepoPath,
            ["diff-tree", "--root", "--shortstat", resolved], cancellation: cancellation);

        var nameStatus = files.Success ? files.Output.Trim() : "";
        if (nameStatus.Length > MaxDiffSummaryChars)
            nameStatus = nameStatus[..MaxDiffSummaryChars] + "\n... 差异摘要过长，已截断 ...";
        var fileCount = files.Success
            ? files.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length
            : 0;
        var fileSummary = stat.Success && !string.IsNullOrWhiteSpace(stat.Output)
            ? stat.Output.Trim()
            : fileCount == 0 ? "无文件变更" : $"{fileCount} 个文件变更";

        AttachBranchRefs([node], graph.AllRefs);
        node = node with { FileSummary = fileSummary, ReleaseId = "", ReleaseStatus = "" };
        var detail = new GraphNodeDetail
        {
            Node = node,
            ParentEdges = BuildEdges([node]),
            FileSummary = fileSummary,
            DiffSummary = nameStatus,
            ReleaseId = "",
            ReleaseStatus = "",
            Evidence = "",
        };
        return (true, $"{node.ShortSha} {node.Subject}；{fileSummary}", detail);
    }

    private async Task<(bool Success, string Message, List<GraphCommitNode> Nodes, List<GraphEdge> Edges, GraphPage Page)>
        LoadCommitPageAsync(GraphScope scope, int limit, int skip, CancellationToken cancellation)
    {
        var args = new List<string>
        {
            "log", "--parents", "--date-order",
            $"--skip={skip}",
            $"--max-count={limit + 1}",
            $"--format={LogFormat}",
        };
        // 引用必须放在路径分隔符 -- 之前，否则会被当成 pathspec。
        args.AddRange(scope.AllRefs.Select(item => item.FullName).Distinct(StringComparer.Ordinal));
        if (!string.IsNullOrWhiteSpace(scope.CutoffSha))
        {
            args.Add("--not");
            args.Add(scope.CutoffSha);
        }

        var result = await GitRunner.RunAsync(scope.RepoPath, args, cancellation: cancellation);
        if (!result.Success)
            return (false, $"读取图谱提交失败:\n{result.Output}", [], [],
                new GraphPage { Skip = skip, Limit = limit });

        var parsed = ParseLog(result.Output);
        var hasMore = parsed.Count > limit;
        if (hasMore)
            parsed = parsed.Take(limit).ToList();
        AttachBranchRefs(parsed, scope.AllRefs);
        var edges = BuildEdges(parsed);
        var page = new GraphPage
        {
            Skip = skip,
            Limit = limit,
            HasMore = hasMore,
            UntilSha = parsed.Count > 0 ? parsed[^1].Sha : null,
        };
        return (true, "", parsed, edges, page);
    }

    private async Task<(bool IsDirty, string Message)> ReadProjectDirtyAsync(
        string name, CancellationToken cancellation)
    {
        var resolved = await _projects.ResolveWorktreeAsync(name);
        if (!resolved.Success || resolved.Worktree == null)
        {
            return (false, string.IsNullOrWhiteSpace(resolved.Message)
                ? "未找到编号项目工作树，工作树状态未知"
                : resolved.Message);
        }

        var statuses = await _projects.ReadWorktreeStatusesAsync([resolved.Worktree], cancellation);
        var info = statuses.Count > 0 ? statuses[0] : resolved.Worktree;
        if (info.IsClean == true)
            return (false, string.IsNullOrWhiteSpace(info.WorktreeStatusMessage) ? "工作树干净" : info.WorktreeStatusMessage);
        if (info.IsClean == false)
            return (true, string.IsNullOrWhiteSpace(info.WorktreeStatusMessage)
                ? "工作树有未提交或未跟踪的文件"
                : info.WorktreeStatusMessage);
        return (false, string.IsNullOrWhiteSpace(info.WorktreeStatusMessage) ? "工作树状态未知" : info.WorktreeStatusMessage);
    }

    private async Task<(bool Success, string Message, bool Reachable)> IsReachableFromTipsAsync(
        string sha, GraphScope scope, CancellationToken cancellation)
    {
        string? lastError = null;
        var reachableFromTip = false;
        foreach (var tip in scope.AllRefs
                     .Select(item => item.TargetSha)
                     .Where(item => item.Length > 0)
                     .Distinct(StringComparer.Ordinal))
        {
            // 只看退出码：0 表示 sha 是 tip 的祖先（含自身）；1 表示不是。
            var result = await GitRunner.RunAsync(scope.RepoPath,
                ["merge-base", "--is-ancestor", sha, tip], cancellation: cancellation);
            if (result.ExitCode == 0)
            {
                reachableFromTip = true;
                break;
            }

            if (result.ExitCode != 1)
                lastError = result.Output;
        }

        if (!reachableFromTip)
        {
            if (lastError != null)
                return (false, $"无法验证提交是否属于项目图谱:\n{lastError}", false);
            return (true, "", false);
        }

        if (string.IsNullOrWhiteSpace(scope.CutoffSha))
            return (true, "", true);

        var cutoff = await GitRunner.RunAsync(scope.RepoPath,
            ["merge-base", "--is-ancestor", sha, scope.CutoffSha], cancellation: cancellation);
        if (cutoff.ExitCode == 0)
            return (true, "", false);
        if (cutoff.ExitCode != 1)
            return (false, $"无法验证提交是否属于项目图谱:\n{cutoff.Output}", false);
        return (true, "", true);
    }

    private async Task<(bool Success, string Message, List<RawRef> Refs)> ListRefsAsync(
        string repo, CancellationToken cancellation)
    {
        var result = await GitRunner.RunAsync(repo,
            ["for-each-ref", "--format=%(objectname)%09%(refname)%09%(refname:short)",
                "refs/heads", "refs/remotes"],
            cancellation: cancellation);
        if (!result.Success)
            return (false, $"读取项目相关引用失败:\n{result.Output}", []);

        var refs = new List<RawRef>();
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length < 3)
                continue;
            var fullName = parts[1].Trim();
            refs.Add(new RawRef(
                parts[0].Trim(),
                fullName,
                parts[2].Trim(),
                fullName.StartsWith("refs/remotes/", StringComparison.OrdinalIgnoreCase)));
        }

        return (true, "", refs);
    }

    private async Task<string> MergeBaseOrEmptyAsync(string repo, string a, string b, CancellationToken cancellation)
    {
        var result = await GitRunner.RunAsync(repo, ["merge-base", a, b],
            cancellation: cancellation);
        if (!result.Success)
            return "";
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim() ?? "";
    }

    private async Task<string?> ResolveCommitAsync(string repo, string value, CancellationToken cancellation)
    {
        var result = await GitRunner.RunAsync(repo,
            ["rev-parse", "--verify", $"{value}^{{commit}}"], cancellation: cancellation);
        return result.Success
            ? result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim()
            : null;
    }

    private static List<GraphCommitNode> ParseLog(string output)
    {
        var nodes = new List<GraphCommitNode>();
        foreach (var record in output.Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
        {
            var node = ParseCommitFields(record.Trim('\r', '\n').Split('\u001f'));
            if (node != null)
                nodes.Add(node);
        }

        return nodes;
    }

    private static GraphCommitNode? ParseShow(string output)
        => ParseCommitFields(output.Trim().Split('\u001f'));

    private static GraphCommitNode? ParseCommitFields(string[] fields)
    {
        if (fields.Length < 6 || !DateTimeOffset.TryParse(fields[3], CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var committedAt))
            return null;
        var parentField = fields[5].Trim();
        var parents = string.IsNullOrWhiteSpace(parentField)
            ? new List<string>()
            : parentField
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .ToList();
        return new GraphCommitNode
        {
            Sha = fields[0].Trim(),
            ShortSha = fields[1].Trim(),
            Parents = parents,
            Author = fields[2].Trim(),
            CommittedAt = committedAt,
            Subject = fields[4].Trim(),
            FileSummary = "",
            ReleaseId = "",
            ReleaseStatus = "",
        };
    }

    private static void AttachBranchRefs(List<GraphCommitNode> nodes, IReadOnlyList<GraphRef> refs)
    {
        var bySha = refs
            .Where(item => item.TargetSha.Length > 0)
            .GroupBy(item => item.TargetSha, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.Name).Distinct(StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);
        for (var i = 0; i < nodes.Count; i++)
        {
            if (bySha.TryGetValue(nodes[i].Sha, out var names))
                nodes[i] = nodes[i] with { BranchRefs = names };
        }
    }

    private static List<GraphEdge> BuildEdges(IReadOnlyList<GraphCommitNode> nodes)
    {
        var edges = new List<GraphEdge>();
        foreach (var node in nodes)
        {
            for (var i = 0; i < node.Parents.Count; i++)
            {
                edges.Add(new GraphEdge
                {
                    FromSha = node.Parents[i],
                    ToSha = node.Sha,
                    IsMergeParent = i > 0,
                });
            }
        }

        return edges;
    }

    private static int ClampLimit(int limit) => Math.Clamp(limit, 1, MaxLimit);

    private static string Short(string sha)
        => sha.Length <= 10 ? sha : sha[..10];

    private sealed record GraphScope(
        string ProjectName,
        GraphRef Mainline,
        List<GraphRef> Parallels,
        List<GraphRef> AllRefs,
        List<GraphBranchRelation> Relations,
        string CutoffSha,
        string RepoPath)
    {
        public List<GraphRef> Lanes()
        {
            var lanes = new List<GraphRef> { Mainline };
            lanes.AddRange(Parallels);
            return lanes;
        }
    }

    private sealed record RawRef(string Sha, string FullName, string ShortName, bool IsRemote);
}
