namespace HistoryJanus.Git;

public sealed record AiBranchAuditEntry(
    string Project, string Branch, string ShortSha, string State,
    bool VisibleInOperations, bool IsCurrent, IReadOnlyList<string> WorktreePaths);

public sealed record AiBranchAuditError(string Project, string Message);

public sealed record AiBranchAuditReport(
    int ScannedProjects, int SkippedArchivedProjects,
    IReadOnlyList<AiBranchAuditEntry> Branches, IReadOnlyList<AiBranchAuditError> Errors);

public sealed partial class BranchService
{
    /// <summary>REQ-BRANCH-024：显式调用的本地全库巡检；不 fetch、不删引用、不管理宿主工作区。</summary>
    public async Task<(bool Success, string Message, AiBranchAuditReport Report)> AuditAsync(
        string? name = null, CancellationToken cancellation = default)
    {
        IReadOnlyList<WorktreeInfo> targets;
        if (!string.IsNullOrWhiteSpace(name))
        {
            var resolved = await projects.ResolveWorktreeAsync(name.Trim());
            if (!resolved.Success || resolved.Worktree == null)
                return (false, resolved.Message, new(0, 0, [], [new(name.Trim(), resolved.Message)]));
            targets = [resolved.Worktree];
        }
        else
        {
            var listed = await projects.ListWorktreesAsync();
            if (!listed.Git.Success)
                return (false, listed.Git.Output, new(0, 0, [], [new("", listed.Git.Output)]));
            targets = listed.Worktrees;
        }

        var entries = new List<AiBranchAuditEntry>();
        var errors = new List<AiBranchAuditError>();
        var scanned = 0;
        foreach (var target in targets.Where(item => !item.IsArchived))
        {
            cancellation.ThrowIfCancellationRequested();
            scanned++;
            // 没有 main 时不可把整个项目误报为已经收口。
            var main = await GitRunner.RunAsync(target.WorktreePath,
                ["rev-parse", "--verify", $"refs/heads/{ProjectService.MainlineBranch}"], cancellation);
            if (!main.Success)
            {
                errors.Add(new(target.BranchName, $"无法读取主线：{main.Output}"));
                continue;
            }
            var listed = await ListAsync(target.BranchName, cancellation);
            if (!listed.Success || listed.Report == null)
            {
                errors.Add(new(target.BranchName, listed.Message));
                continue;
            }
            var worktrees = await GitRunner.RunAsync(target.WorktreePath,
                ["-c", "core.quotePath=false", "worktree", "list", "--porcelain"], cancellation);
            if (!worktrees.Success)
                errors.Add(new(target.BranchName, $"无法读取工作区：{worktrees.Output}"));
            var paths = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var path = "";
            foreach (var line in worktrees.Success ? worktrees.Output.Split('\n') : [])
            {
                var text = line.TrimEnd('\r');
                if (text.StartsWith("worktree ", StringComparison.Ordinal))
                    path = text[9..];
                if (text.StartsWith("branch refs/heads/", StringComparison.Ordinal))
                {
                    var branch = text[18..];
                    if (!paths.TryGetValue(branch, out var branchPaths))
                        paths[branch] = branchPaths = [];
                    branchPaths.Add(path);
                }
            }
            foreach (var branch in listed.Report.Branches.Where(item => IsAiBranch(item.Name)))
            {
                var attached = paths.TryGetValue(branch.Name, out var found) ? found : [];
                entries.Add(new(target.BranchName, branch.Name, branch.ShortSha,
                    branch.MergedIntoMain ? "integrated" : attached.Count > 0 ? "working" : "review",
                    branch.IsCurrent || !branch.MergedIntoMain, branch.IsCurrent, attached));
            }
        }
        var report = new AiBranchAuditReport(scanned, targets.Count(item => item.IsArchived), entries, errors);
        var integrated = entries.Count(item => item.State == "integrated");
        var working = entries.Count(item => item.State == "working");
        var review = entries.Count(item => item.State == "review");
        var message = $"已检查 {scanned} 个本地项目，跳过 {report.SkippedArchivedProjects} 个归档项目；"
            + $"AI 分支 {entries.Count} 条：已收口 {integrated}、工作区中 {working}、待核查 {review}；失败 {errors.Count}。"
            + "\nreview 表示尚未证明已收口且没有检出工作区（含刚创建的分支），不能自动删除或合并。"
            + string.Concat(entries.Select(item => $"\n{item.Project} | {item.State} | {item.Branch}"))
            + string.Concat(errors.Select(item => $"\n检查失败 {item.Project}：{item.Message}"));
        return (errors.Count == 0, message, report);
    }

    private static bool IsAiBranch(string branch)
        => new[] { "ai/", "codex/", "claude/", "grok/", "gemini/" }
            .Any(prefix => branch.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
