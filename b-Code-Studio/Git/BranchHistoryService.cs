using System.Globalization;

namespace HistoryJanus.Git;

public enum BranchRemoteState
{
    Unknown,
    InSync,
    Ahead,
    Behind,
    Diverged,
}

public enum CommitRemoteState
{
    Unknown,
    Pushed,
    LocalOnly,
}

public sealed record BranchHistoryEntry(
    string Sha,
    string ShortSha,
    string Author,
    DateTimeOffset CommittedAt,
    string Subject,
    int ParentCount,
    bool IsForkPoint,
    bool IsHead,
    CommitRemoteState RemoteState)
{
    public bool IsMerge => ParentCount > 1;
    public string Marker => IsForkPoint ? "分叉点" : IsHead ? "HEAD" : IsMerge ? "合并" : "";
    public string TimeDisplay => CommittedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string RemoteDisplay => RemoteState switch
    {
        CommitRemoteState.Pushed => "已推送",
        CommitRemoteState.LocalOnly => "仅本地",
        _ => "远端未知",
    };
}

public sealed record BranchHistoryReport(
    string Branch,
    string? ParentBranch,
    string ForkSha,
    string HeadSha,
    string? RemoteHeadSha,
    BranchRemoteState RemoteState,
    int AheadCount,
    int BehindCount,
    int TotalOwnCommits,
    int Skip,
    int Limit,
    bool HasMore,
    bool RemoteRefreshFailed,
    string? RemoteMessage,
    IReadOnlyList<BranchHistoryEntry> Entries)
{
    public string ParentDisplay => ParentBranch ?? "(基础分支)";
    public string ForkShortSha => Short(ForkSha);
    public string HeadShortSha => Short(HeadSha);
    public string RemoteDisplay => RemoteState switch
    {
        BranchRemoteState.InSync => "与 origin 一致",
        BranchRemoteState.Ahead => $"领先 origin {AheadCount}",
        BranchRemoteState.Behind => $"落后 origin {BehindCount}",
        BranchRemoteState.Diverged => $"已分叉：领先 {AheadCount} / 落后 {BehindCount}",
        _ => "远端未知",
    };

    private static string Short(string sha) => sha.Length <= 10 ? sha : sha[..10];
}

public sealed record CommitFileChange(string Status, string Path, string? OldPath = null)
{
    public string DisplayPath => OldPath == null ? Path : $"{OldPath} -> {Path}";
}

public sealed record CommitDetail(
    string Branch,
    string Sha,
    string ShortSha,
    string Author,
    string AuthorEmail,
    DateTimeOffset CommittedAt,
    string Subject,
    string Body,
    IReadOnlyList<string> Parents,
    IReadOnlyList<CommitFileChange> Files)
{
    public string TimeDisplay => CommittedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}

public sealed record BranchDiffReport(
    string Branch,
    string TargetSha,
    string HeadSha,
    int CommitCount,
    int FileCount,
    string ShortStat,
    string DiffText,
    bool DiffTruncated,
    IReadOnlyList<CommitFileChange> Files);

public sealed record BranchMutationReport(
    bool Success,
    bool Changed,
    string Branch,
    string? TargetSha,
    string? BeforeSha,
    string? AfterSha,
    string Message);

/// <summary>
/// 分支历史与安全回滚。Git 提交图是历史真值；所有进程调用仍统一经过 GitRunner。
/// </summary>
public sealed class BranchHistoryService
{
    private const int MaxLimit = 2000;
    private const int MaxDiffChars = 200_000;
    private const string LogFormat = "%H%x1f%h%x1f%an%x1f%aI%x1f%s%x1f%P%x1e";

    private readonly ProjectService _projects;
    private readonly object _approvalGate = new();
    private readonly Dictionary<string, string> _rollbackApprovals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ForcePushApproval> _forcePushApprovals = new(StringComparer.OrdinalIgnoreCase);

    public BranchHistoryService(ProjectService projects)
    {
        _projects = projects;
    }

    public async Task<(bool Success, string Message, BranchHistoryReport? Report)> GetHistoryAsync(
        string name,
        int limit = 200,
        int skip = 0,
        bool refreshTree = false,
        bool refreshRemote = false,
        IProgress<string>? progress = null,
        CancellationToken cancellation = default)
    {
        name = name.Trim();
        limit = Math.Clamp(limit, 1, MaxLimit);
        skip = Math.Max(0, skip);

        var boundaryResult = await ResolveBoundaryAsync(name, refreshTree, progress, cancellation);
        if (!boundaryResult.Success || boundaryResult.Boundary == null)
            return (false, boundaryResult.Message, null);
        var boundary = boundaryResult.Boundary;

        string? remoteWarning = null;
        if (refreshRemote)
        {
            progress?.Report($"刷新 origin/{ProjectService.MainlineBranch}...");
            var fetch = await GitRunner.RunAsync(boundary.RepoPath,
                ["fetch", "--no-tags", "origin",
                    $"+refs/heads/{ProjectService.MainlineBranch}:refs/remotes/origin/{ProjectService.MainlineBranch}"],
                cancellation: cancellation);
            if (!fetch.Success)
                remoteWarning = fetch.Output;
        }

        var remote = await ReadRemoteAsync(boundary.RepoPath, boundary.HeadSha, cancellation);
        var ownResult = await GitRunner.RunAsync(boundary.RepoPath,
            ["log", "--first-parent", "--reverse", $"--format={LogFormat}",
                $"{boundary.ForkSha}..{boundary.HeadSha}"],
            cancellation: cancellation);
        if (!ownResult.Success)
            return (false, $"读取分支历史失败:\n{ownResult.Output}", null);

        var ownEntries = ParseLog(ownResult.Output);
        var total = ownEntries.Count;
        var pageEnd = Math.Max(0, total - Math.Min(skip, total));
        var pageStart = Math.Max(0, pageEnd - limit);
        var page = ownEntries.Skip(pageStart).Take(pageEnd - pageStart).ToList();

        var forkEntryResult = await ReadCommitEntryAsync(boundary.RepoPath, boundary.ForkSha, cancellation);
        if (!forkEntryResult.Success || forkEntryResult.Entry == null)
            return (false, forkEntryResult.Message, null);

        var entries = new List<BranchHistoryEntry>(page.Count + 1)
        {
            WithState(forkEntryResult.Entry with { IsForkPoint = true }, remote.Commits),
        };
        entries.AddRange(page.Select(entry => WithState(
            entry with { IsHead = entry.Sha.Equals(boundary.HeadSha, StringComparison.Ordinal) },
            remote.Commits)));

        var report = new BranchHistoryReport(
            name,
            boundary.ParentBranch,
            boundary.ForkSha,
            boundary.HeadSha,
            remote.HeadSha,
            remote.State,
            remote.Ahead,
            remote.Behind,
            total,
            skip,
            limit,
            pageStart > 0,
            remoteWarning != null,
            remoteWarning ?? remote.Message,
            entries);

        var message = $"{name}：分叉点 {report.ForkShortSha}，自有提交 {total} 条，" +
                      $"本次显示 {entries.Count} 个节点，{report.RemoteDisplay}";
        if (remoteWarning != null)
            message += "；远端刷新失败，已使用本地跟踪状态";
        return (true, message, report);
    }

    public async Task<(bool Success, string Message, CommitDetail? Detail)> GetCommitAsync(
        string name, string sha, CancellationToken cancellation = default)
    {
        var targetResult = await ResolveAllowedTargetAsync(name, sha, cancellation);
        if (!targetResult.Success || targetResult.Target == null)
            return (false, targetResult.Message, null);
        var target = targetResult.Target;

        const string format = "%H%x1f%h%x1f%an%x1f%ae%x1f%aI%x1f%s%x1f%b%x1f%P";
        var meta = await GitRunner.RunAsync(target.RepoPath,
            ["show", "-s", $"--format={format}", target.TargetSha], cancellation: cancellation);
        if (!meta.Success)
            return (false, $"读取提交详情失败:\n{meta.Output}", null);
        var fields = meta.Output.Split('\u001f');
        if (fields.Length < 8 || !DateTimeOffset.TryParse(fields[4], CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var committedAt))
            return (false, "Git 返回的提交详情格式无效", null);

        var filesResult = await GitRunner.RunAsync(target.RepoPath,
            ["diff-tree", "--root", "--no-commit-id", "--name-status", "-r", target.TargetSha],
            cancellation: cancellation);
        var files = filesResult.Success ? ParseNameStatus(filesResult.Output) : [];
        var detail = new CommitDetail(
            name,
            fields[0].Trim(),
            fields[1].Trim(),
            fields[2].Trim(),
            fields[3].Trim(),
            committedAt,
            fields[5].Trim(),
            fields[6].Trim(),
            fields[7].Split(' ', StringSplitOptions.RemoveEmptyEntries),
            files);
        return (true, $"{detail.ShortSha} {detail.Subject}；变更文件 {files.Count} 个", detail);
    }

    public async Task<(bool Success, string Message, BranchDiffReport? Report)> GetDiffAsync(
        string name, string sha, CancellationToken cancellation = default)
    {
        var targetResult = await ResolveAllowedTargetAsync(name, sha, cancellation);
        if (!targetResult.Success || targetResult.Target == null)
            return (false, targetResult.Message, null);
        var target = targetResult.Target;

        var countResult = await GitRunner.RunAsync(target.RepoPath,
            ["rev-list", "--first-parent", "--count", $"{target.TargetSha}..{target.HeadSha}"],
            cancellation: cancellation);
        var fileResult = await GitRunner.RunAsync(target.RepoPath,
            ["diff", "--name-status", target.TargetSha, target.HeadSha], cancellation: cancellation);
        var statResult = await GitRunner.RunAsync(target.RepoPath,
            ["diff", "--shortstat", target.TargetSha, target.HeadSha], cancellation: cancellation);
        var diffResult = await GitRunner.RunAsync(target.RepoPath,
            ["diff", "--no-ext-diff", "--no-color", "--unified=3", target.TargetSha, target.HeadSha],
            cancellation: cancellation);
        if (!countResult.Success || !fileResult.Success || !diffResult.Success)
            return (false, "无法生成目标提交到当前 HEAD 的差异", null);

        _ = int.TryParse(countResult.Output.Trim(), out var commitCount);
        var files = ParseNameStatus(fileResult.Output);
        var diff = diffResult.Output;
        var truncated = diff.Length > MaxDiffChars;
        if (truncated)
            diff = diff[..MaxDiffChars] + "\n\n... 差异过长，已截断 ...";

        var report = new BranchDiffReport(name, target.TargetSha, target.HeadSha,
            commitCount, files.Count, statResult.Success ? statResult.Output.Trim() : "",
            diff, truncated, files);
        return (true, $"{name}：目标后有 {commitCount} 个提交，影响 {files.Count} 个文件", report);
    }

    /// <summary>CommandDescriptor 的同步确认合同使用；执行阶段仍会完整异步重验。</summary>
    public string? BuildRollbackPrompt(string name, string sha, bool hardReset)
    {
        if (hardReset && _projects.IsProtected(name))
            return null;
        var diff = GetDiffAsync(name, sha).ConfigureAwait(false).GetAwaiter().GetResult();
        if (!diff.Success || diff.Report == null)
            return null;
        var report = diff.Report;
        lock (_approvalGate)
            _rollbackApprovals[RollbackApprovalKey(name, sha, hardReset)] = report.HeadSha;
        var action = hardReset ? "硬重置本地分支" : "生成恢复提交";
        var warning = hardReset
            ? "此操作会改写本地分支指针；不会修改远端，后续可能需要独立强制推送。"
            : "此操作会新增一个恢复提交，原有提交历史仍保留。";
        return $"确定要{action}吗？\n\n" +
               $"项目：{name}\n当前：{Short(report.HeadSha)}\n目标：{Short(report.TargetSha)}\n" +
               $"受影响提交：{report.CommitCount}\n受影响文件：{report.FileCount}\n\n{warning}";
    }

    public string? BuildForcePushPrompt(string name)
    {
        if (_projects.IsProtected(name))
            return null;
        var worktree = _projects.ResolveWorktreeAsync(name).ConfigureAwait(false).GetAwaiter().GetResult();
        if (!worktree.Success || worktree.Worktree == null)
            return null;
        var repo = worktree.Worktree.WorktreePath;
        var local = ResolveRefAsync(repo, $"refs/heads/{ProjectService.MainlineBranch}").ConfigureAwait(false).GetAwaiter().GetResult();
        var remote = ResolveRefAsync(repo, $"refs/remotes/origin/{ProjectService.MainlineBranch}").ConfigureAwait(false).GetAwaiter().GetResult();
        if (local == null || remote == null)
            return null;
        lock (_approvalGate)
            _forcePushApprovals[name.Trim()] = new ForcePushApproval(local, remote);
        return $"确定要使用 --force-with-lease 改写远端分支吗？\n\n" +
               $"项目：{name}\n本地：{Short(local)}\n预期远端：{Short(remote)}\n\n" +
               "若远端已被其他人更新，lease 校验会拒绝推送；程序绝不会降级为 --force。";
    }

    public async Task<BranchMutationReport> RollbackAsync(
        string name,
        string sha,
        string message,
        CancellationToken cancellation = default,
        string? expectedHead = null)
    {
        if (string.IsNullOrWhiteSpace(message))
            return FailMutation(name, sha, "恢复提交说明不能为空");
        var preflight = await PrepareMutationAsync(name, sha, allowProtected: true, cancellation);
        if (!preflight.Success || preflight.Context == null)
            return FailMutation(name, sha, preflight.Message);
        var ctx = preflight.Context;
        if (expectedHead != null && !ctx.BeforeSha.Equals(expectedHead, StringComparison.Ordinal))
            return FailMutation(name, ctx.TargetSha,
                $"确认后分支 HEAD 已变化：{Short(expectedHead)} -> {Short(ctx.BeforeSha)}，请重新预览并确认",
                ctx.BeforeSha);

        var quiet = await GitRunner.RunAsync(ctx.WorktreePath,
            ["diff", "--quiet", ctx.TargetSha, ctx.BeforeSha], cancellation: cancellation);
        if (quiet.ExitCode == 0)
            return new BranchMutationReport(true, false, name, ctx.TargetSha, ctx.BeforeSha,
                ctx.BeforeSha, "目标提交与当前 HEAD 内容一致，无需生成恢复提交");
        if (quiet.ExitCode != 1)
            return FailMutation(name, ctx.TargetSha, $"比较目标内容失败:\n{quiet.Output}", ctx.BeforeSha);

        var restore = await GitRunner.RunAsync(ctx.WorktreePath,
            ["restore", $"--source={ctx.TargetSha}", "--staged", "--worktree", "--", "."],
            cancellation: cancellation);
        if (!restore.Success)
            return FailMutation(name, ctx.TargetSha, $"恢复目标内容失败:\n{restore.Output}", ctx.BeforeSha);

        var commit = await GitRunner.RunAsync(ctx.WorktreePath,
            ["commit", "-m", message.Trim()], cancellation: cancellation);
        if (!commit.Success)
        {
            await GitRunner.RunAsync(ctx.WorktreePath, ["reset", "--hard", ctx.BeforeSha],
                cancellation: CancellationToken.None);
            return FailMutation(name, ctx.TargetSha,
                $"创建恢复提交失败，已恢复到操作前 HEAD:\n{commit.Output}", ctx.BeforeSha);
        }

        var after = await ResolveRefAsync(ctx.WorktreePath, "HEAD", cancellation) ?? ctx.BeforeSha;
        return new BranchMutationReport(true, true, name, ctx.TargetSha, ctx.BeforeSha, after,
            $"已生成恢复提交 {Short(after)}，目标内容 {Short(ctx.TargetSha)}；原历史仍保留");
    }

    public async Task<BranchMutationReport> ResetAsync(
        string name,
        string sha,
        CancellationToken cancellation = default,
        string? expectedHead = null)
    {
        if (_projects.IsProtected(name))
            return FailMutation(name, sha, $"受保护分支 {name} 禁止硬重置");
        var preflight = await PrepareMutationAsync(name, sha, allowProtected: false, cancellation);
        if (!preflight.Success || preflight.Context == null)
            return FailMutation(name, sha, preflight.Message);
        var ctx = preflight.Context;
        if (expectedHead != null && !ctx.BeforeSha.Equals(expectedHead, StringComparison.Ordinal))
            return FailMutation(name, ctx.TargetSha,
                $"确认后分支 HEAD 已变化：{Short(expectedHead)} -> {Short(ctx.BeforeSha)}，请重新预览并确认",
                ctx.BeforeSha);
        if (ctx.TargetSha.Equals(ctx.BeforeSha, StringComparison.Ordinal))
            return new BranchMutationReport(true, false, name, ctx.TargetSha, ctx.BeforeSha,
                ctx.BeforeSha, "当前 HEAD 已经位于目标提交，无需硬重置");

        var reset = await GitRunner.RunAsync(ctx.WorktreePath,
            ["reset", "--hard", ctx.TargetSha], cancellation: cancellation);
        if (!reset.Success)
            return FailMutation(name, ctx.TargetSha, $"硬重置失败:\n{reset.Output}", ctx.BeforeSha);
        return new BranchMutationReport(true, true, name, ctx.TargetSha, ctx.BeforeSha, ctx.TargetSha,
            $"本地分支已硬重置到 {Short(ctx.TargetSha)}；远端未修改。如需改写远端，请单独执行 janus.history.forcepush name={name}");
    }

    public async Task<BranchMutationReport> ForcePushAsync(
        string name,
        CancellationToken cancellation = default,
        string? expectedLocal = null,
        string? expectedRemote = null)
    {
        name = name.Trim();
        if (_projects.IsProtected(name))
            return FailMutation(name, null, $"受保护分支 {name} 禁止强制推送");
        var worktree = await _projects.ResolveWorktreeAsync(name);
        if (!worktree.Success || worktree.Worktree == null)
            return FailMutation(name, null, worktree.Message);
        var local = await ResolveRefAsync(worktree.Worktree.WorktreePath,
            $"refs/heads/{ProjectService.MainlineBranch}", cancellation);
        var remote = await ResolveRefAsync(worktree.Worktree.WorktreePath,
            $"refs/remotes/origin/{ProjectService.MainlineBranch}", cancellation);
        if (local == null)
            return FailMutation(name, null, $"本地分支不存在: {ProjectService.MainlineBranch}");
        if (remote == null)
            return FailMutation(name, null, $"不存在 origin/{ProjectService.MainlineBranch} 跟踪引用，拒绝无 lease 强推");
        if (expectedLocal != null && !local.Equals(expectedLocal, StringComparison.Ordinal))
            return FailMutation(name, local,
                $"确认后本地 HEAD 已变化：{Short(expectedLocal)} -> {Short(local)}，请重新确认", remote);
        if (expectedRemote != null && !remote.Equals(expectedRemote, StringComparison.Ordinal))
            return FailMutation(name, local,
                $"确认后 origin 跟踪引用已变化：{Short(expectedRemote)} -> {Short(remote)}，请重新确认", remote);

        var push = await GitRunner.RunAsync(worktree.Worktree.WorktreePath,
            ["push", $"--force-with-lease=refs/heads/{ProjectService.MainlineBranch}:{remote}", "origin",
                $"refs/heads/{ProjectService.MainlineBranch}:refs/heads/{ProjectService.MainlineBranch}"],
            cancellation: cancellation);
        if (!push.Success)
            return FailMutation(name, local, $"--force-with-lease 推送失败:\n{push.Output}", remote);
        return new BranchMutationReport(true, true, name, local, remote, local,
            $"已使用 --force-with-lease 更新 origin/{ProjectService.MainlineBranch}: {Short(remote)} -> {Short(local)}");
    }

    private async Task<(bool Success, string Message, MutationContext? Context)> PrepareMutationAsync(
        string name, string sha, bool allowProtected, CancellationToken cancellation)
    {
        name = name.Trim();
        if (!allowProtected && _projects.IsProtected(name))
            return (false, $"受保护分支 {name} 禁止此操作", null);
        var worktree = await _projects.ResolveWorktreeAsync(name);
        if (!worktree.Success || worktree.Worktree == null)
            return (false, worktree.Message, null);
        var status = await GitRunner.RunAsync(worktree.Worktree.WorktreePath,
            ["status", "--porcelain=v1", "--untracked-files=all"], cancellation: cancellation);
        if (!status.Success)
            return (false, $"检查工作树状态失败:\n{status.Output}", null);
        if (!string.IsNullOrWhiteSpace(status.Output))
            return (false, "工作树存在未提交或未跟踪文件；请先提交、移走或清理后再操作", null);

        var target = await ResolveAllowedTargetAsync(name, sha, cancellation);
        if (!target.Success || target.Target == null)
            return (false, target.Message, null);
        return (true, "预检通过", new MutationContext(worktree.Worktree.WorktreePath,
            target.Target.TargetSha, target.Target.HeadSha));
    }

    private async Task<(bool Success, string Message, AllowedTarget? Target)> ResolveAllowedTargetAsync(
        string name, string sha, CancellationToken cancellation)
    {
        var boundaryResult = await ResolveBoundaryAsync(name.Trim(), refreshTree: false,
            progress: null, cancellation);
        if (!boundaryResult.Success || boundaryResult.Boundary == null)
            return (false, boundaryResult.Message, null);
        var boundary = boundaryResult.Boundary;
        var target = await ResolveRefAsync(boundary.RepoPath, sha.Trim(), cancellation);
        if (target == null)
            return (false, $"无法解析提交对象: {sha}", null);

        var allowedResult = await GitRunner.RunAsync(boundary.RepoPath,
            ["rev-list", "--first-parent", $"{boundary.ForkSha}..{boundary.HeadSha}"],
            cancellation: cancellation);
        if (!allowedResult.Success)
            return (false, "无法验证目标提交范围", null);
        var allowed = new HashSet<string>(allowedResult.Output.Split('\n',
            StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim()), StringComparer.Ordinal)
        {
            boundary.ForkSha,
        };
        if (!allowed.Contains(target))
            return (false, "目标提交不在该分支的分叉点至当前 HEAD 第一父链范围内", null);
        return (true, "目标有效", new AllowedTarget(target, boundary.HeadSha, boundary.ForkSha, boundary.RepoPath));
    }

    private async Task<(bool Success, string Message, BranchBoundary? Boundary)> ResolveBoundaryAsync(
        string name,
        bool refreshTree,
        IProgress<string>? progress,
        CancellationToken cancellation)
    {
        if (name.Length == 0)
            return (false, "项目名称不能为空", null);
        var worktree = await _projects.ResolveWorktreeAsync(name);
        if (!worktree.Success || worktree.Worktree == null)
            return (false, worktree.Message, null);
        var repo = worktree.Worktree.WorktreePath;
        var head = await ResolveRefAsync(repo, $"refs/heads/{ProjectService.MainlineBranch}", cancellation)
                   ?? await ResolveRefAsync(repo, "HEAD", cancellation);
        if (head == null)
            return (false, $"无法读取项目 {name} 的 HEAD", null);

        _ = refreshTree;
        _ = progress;
        var parentBranch = ProjectRepoLayout.ReadTemplateSource(repo);
        var roots = await GitRunner.RunAsync(repo,
            ["rev-list", "--max-parents=0", "--reverse", "HEAD"], cancellation: cancellation);
        var fork = roots.Success
            ? roots.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim()
            : null;
        if (string.IsNullOrWhiteSpace(fork))
            return (false, $"无法确定项目 {name} 的根提交", null);
        return (true, "边界已解析", new BranchBoundary(name, parentBranch, fork, head, repo));
    }

    private async Task<RemoteSnapshot> ReadRemoteAsync(
        string repo, string localHead, CancellationToken cancellation)
    {
        var remoteHead = await ResolveRefAsync(repo, $"refs/remotes/origin/{ProjectService.MainlineBranch}", cancellation);
        if (remoteHead == null)
            return new RemoteSnapshot(null, BranchRemoteState.Unknown, 0, 0,
                new HashSet<string>(StringComparer.Ordinal), "不存在本地 origin 跟踪引用");

        var counts = await GitRunner.RunAsync(repo,
            ["rev-list", "--left-right", "--count", $"{localHead}...{remoteHead}"],
            cancellation: cancellation);
        var ahead = 0;
        var behind = 0;
        if (counts.Success)
        {
            var parts = counts.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                _ = int.TryParse(parts[0], out ahead);
                _ = int.TryParse(parts[1], out behind);
            }
        }

        var remoteHistory = await GitRunner.RunAsync(repo,
            ["rev-list", remoteHead], cancellation: cancellation);
        var commits = remoteHistory.Success
            ? new HashSet<string>(remoteHistory.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim()), StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var state = ahead == 0 && behind == 0 ? BranchRemoteState.InSync
            : ahead > 0 && behind == 0 ? BranchRemoteState.Ahead
            : ahead == 0 && behind > 0 ? BranchRemoteState.Behind
            : BranchRemoteState.Diverged;
        return new RemoteSnapshot(remoteHead, state, ahead, behind, commits, null);
    }

    private async Task<(bool Success, string Message, BranchHistoryEntry? Entry)> ReadCommitEntryAsync(
        string repo, string sha, CancellationToken cancellation)
    {
        var result = await GitRunner.RunAsync(repo,
            ["show", "-s", $"--format={LogFormat}", sha], cancellation: cancellation);
        if (!result.Success)
            return (false, $"读取分叉提交失败:\n{result.Output}", null);
        var entry = ParseLog(result.Output).FirstOrDefault();
        return entry == null
            ? (false, "Git 返回的分叉提交格式无效", null)
            : (true, "提交已读取", entry);
    }

    private static async Task<string?> ResolveRefAsync(
        string repo, string value, CancellationToken cancellation = default)
    {
        var result = await GitRunner.RunAsync(repo,
            ["rev-parse", "--verify", $"{value}^{{commit}}"], cancellation: cancellation);
        return result.Success
            ? result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim()
            : null;
    }

    private static List<BranchHistoryEntry> ParseLog(string output)
    {
        var entries = new List<BranchHistoryEntry>();
        foreach (var record in output.Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = record.Trim('\r', '\n').Split('\u001f');
            if (fields.Length < 6 || !DateTimeOffset.TryParse(fields[3], CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var committedAt))
                continue;
            var parents = fields[5].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            entries.Add(new BranchHistoryEntry(fields[0].Trim(), fields[1].Trim(), fields[2].Trim(),
                committedAt, fields[4].Trim(), parents.Length, false, false, CommitRemoteState.Unknown));
        }
        return entries;
    }

    private static List<CommitFileChange> ParseNameStatus(string output)
    {
        var files = new List<CommitFileChange>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length < 2)
                continue;
            if ((parts[0].StartsWith('R') || parts[0].StartsWith('C')) && parts.Length >= 3)
                files.Add(new CommitFileChange(parts[0], parts[2], parts[1]));
            else
                files.Add(new CommitFileChange(parts[0], parts[1]));
        }
        return files;
    }

    private static CommitRemoteState StateOf(string sha, HashSet<string> remoteCommits)
        => remoteCommits.Count == 0 ? CommitRemoteState.Unknown
            : remoteCommits.Contains(sha) ? CommitRemoteState.Pushed
            : CommitRemoteState.LocalOnly;

    private static BranchHistoryEntry WithState(BranchHistoryEntry entry, HashSet<string> remoteCommits)
        => entry with { RemoteState = StateOf(entry.Sha, remoteCommits) };

    private static string Short(string? sha)
        => string.IsNullOrWhiteSpace(sha) ? "(无)" : sha.Length <= 10 ? sha : sha[..10];

    private static BranchMutationReport FailMutation(
        string branch, string? target, string message, string? before = null)
        => new(false, false, branch, target, before, before, message);

    public string? TakeRollbackApproval(string name, string sha, bool hardReset)
    {
        lock (_approvalGate)
        {
            var key = RollbackApprovalKey(name, sha, hardReset);
            if (!_rollbackApprovals.Remove(key, out var head))
                return null;
            return head;
        }
    }

    public (string? Local, string? Remote) TakeForcePushApproval(string name)
    {
        lock (_approvalGate)
        {
            if (!_forcePushApprovals.Remove(name.Trim(), out var approval))
                return (null, null);
            return (approval.Local, approval.Remote);
        }
    }

    private static string RollbackApprovalKey(string name, string sha, bool hardReset)
        => $"{(hardReset ? "reset" : "rollback")}|{name.Trim()}|{sha.Trim()}";

    private sealed record BranchBoundary(
        string Branch, string? ParentBranch, string ForkSha, string HeadSha, string RepoPath);
    private sealed record AllowedTarget(string TargetSha, string HeadSha, string ForkSha, string RepoPath);
    private sealed record MutationContext(string WorktreePath, string TargetSha, string BeforeSha);
    private sealed record RemoteSnapshot(
        string? HeadSha,
        BranchRemoteState State,
        int Ahead,
        int Behind,
        HashSet<string> Commits,
        string? Message);
    private sealed record ForcePushApproval(string Local, string Remote);
}
