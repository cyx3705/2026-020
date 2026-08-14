using System.IO;
using System.Text;

namespace HistoryJanus.Git;

/// <summary>
/// ProjectService 的提交与推送切面。提交链路与确认通道、gitlink 子模块发现、
/// 文件大小阈值和 LFS 预检共享状态，因此保留在同一个 partial 类型中。
/// 子仓库先于父仓库提交，target 决定操作范围。
/// </summary>
public sealed partial class ProjectService
{
    // ---------------------------------------------------------------- 提交

    public async Task<CommitReport> CommitAsync(
        string name,
        string message,
        IProgress<string>? progress,
        bool includeSubmodules = false,
        string? submoduleMessage = null,
        CancellationToken cancellation = default)
        => await CommitAsync(name, message, progress,
            includeSubmodules ? RepositoryTarget.Both : RepositoryTarget.Parent,
            submoduleMessage, cancellation);

    public async Task<CommitReport> CommitAsync(
        string name,
        string message,
        IProgress<string>? progress,
        RepositoryTarget target,
        string? submoduleMessage = null,
        CancellationToken cancellation = default)
    {
        name = name.Trim();
        var (resolved, resolveMessage, worktree) = await ResolveWorktreeAsync(name);
        if (!resolved || worktree == null)
            return new CommitReport(CommitOutcome.Failed, resolveMessage, Target: target);
        return await CommitWorktreeAsync(worktree, message, progress,
            target, submoduleMessage, cancellation);
    }

    private sealed record RepositoryCommitPlan(
        WorktreeInfo Worktree,
        string Message,
        FileSizeCheckStatus CheckStatus,
        List<LargeFileEntry> LargeFiles,
        List<LargeFileEntry> NeedsLfs,
        string BeforeSha,
        bool AddAll);

    private async Task<CommitReport> CommitWorktreeAsync(
        WorktreeInfo worktree,
        string commitMessage,
        IProgress<string>? progress,
        RepositoryTarget target,
        string? submoduleMessage = null,
        CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(commitMessage))
            return new CommitReport(CommitOutcome.Failed, "提交描述不能为空", Target: target);

        List<GitlinkDescriptor> links = [];
        if (target != RepositoryTarget.Parent)
        {
            var discovered = await _gitlinks.DiscoverAsync(worktree.WorktreePath, cancellation);
            if (!discovered.Success)
                return new CommitReport(CommitOutcome.Failed, discovered.Message, Target: target);
            links = discovered.Items;
            var invalid = links.FirstOrDefault(link => string.IsNullOrWhiteSpace(link.Branch)
                                                      || !link.HasIdentity || link.NestedGitlinkDirty);
            if (invalid != null)
            {
                var reason = string.IsNullOrWhiteSpace(invalid.Branch)
                    ? "处于 detached HEAD"
                    : !invalid.HasIdentity
                        ? "缺少 user.name 或 user.email"
                        : "存在发生变化的第二层 gitlink";
                return new CommitReport(CommitOutcome.Failed,
                    $"子模块预检失败 [{invalid.RelativePath}]: {reason}", Target: target);
            }
        }

        var childMessage = string.IsNullOrWhiteSpace(submoduleMessage)
            ? commitMessage.Trim()
            : submoduleMessage.Trim();
        var childPlans = new List<(GitlinkDescriptor Link, RepositoryCommitPlan Plan)>();
        foreach (var link in links.Where(link => link.IsDirty))
        {
            var prepared = await PrepareCommitPlanAsync(
                new WorktreeInfo(link.RelativePath, link.FullPath), childMessage, progress,
                excludedDirectories: [], addAll: true, cancellation);
            if (prepared.Error != null)
                return prepared.Error with { Submodules = BuildSkippedEntries(links), Target = target };
            childPlans.Add((link, prepared.Plan!));
        }

        RepositoryCommitPlan? parentPlan = null;
        if (target != RepositoryTarget.Submodules)
        {
            var parentPrepared = await PrepareCommitPlanAsync(worktree, commitMessage.Trim(), progress,
                links.Select(link => link.FullPath), addAll: false, cancellation);
            if (parentPrepared.Error != null)
                return parentPrepared.Error with
                {
                    Submodules = BuildSkippedEntries(links),
                    Target = target,
                    ParentExecuted = true,
                };
            parentPlan = parentPrepared.Plan!;
        }

        var parentBefore = parentPlan?.BeforeSha
                           ?? await ReadHeadShaAsync(worktree.WorktreePath, cancellation);

        if (childPlans.Count > 0
            && !_confirm(BuildSubmoduleCommitPrompt(worktree.BranchName, childPlans, target)))
        {
            return new CommitReport(CommitOutcome.Rejected, "用户取消子模块联动提交",
                BeforeSha: parentBefore, AfterSha: parentBefore,
                Submodules: BuildSkippedEntries(links), Target: target);
        }

        var entries = BuildSkippedEntries(links).ToList();
        foreach (var (link, plan) in childPlans)
        {
            progress?.Report($"[{worktree.BranchName}/{link.RelativePath}] 提交子模块...");
            var child = await ExecuteCommitPlanAsync(plan, progress, cancellation);
            var entry = new SubmoduleOperationEntry(link.RelativePath, link.Branch,
                child.BeforeSha, child.AfterSha, ToSubmoduleOutcome(child.Outcome), child.Message, link.Kind);
            ReplaceEntry(entries, entry);
            if (child.Outcome is not CommitOutcome.Success and not CommitOutcome.Skipped)
            {
                var pending = await HasPendingParentPointerAsync(
                    worktree.WorktreePath, links, cancellation);
                return new CommitReport(child.Outcome,
                    $"子模块提交失败 [{link.RelativePath}]: {child.Message}",
                    child.HasSizeWarning, child.RejectedFiles, parentBefore,
                    parentBefore, entries,
                    entries.Any(item => item.Outcome == SubmoduleOperationOutcome.Success),
                    target, ParentExecuted: false, ParentPointerPending: pending);
            }
        }

        if (target == RepositoryTarget.Submodules)
        {
            var anyCommitted = entries.Any(item => item.Outcome == SubmoduleOperationOutcome.Success);
            var pending = await HasPendingParentPointerAsync(
                worktree.WorktreePath, links, cancellation);
            var message = anyCommitted
                ? $"已提交 {entries.Count(item => item.Outcome == SubmoduleOperationOutcome.Success)} 个子模块；" +
                  "父分支尚未记录新 gitlink，请执行“分支及子模块”完成收口"
                : links.Count == 0
                    ? "未发现直属子模块，已跳过"
                    : pending
                        ? "子模块没有新变更；父分支仍有待收口 gitlink"
                        : "子模块均无变更，已跳过";
            return new CommitReport(
                anyCommitted ? CommitOutcome.Success : CommitOutcome.Skipped,
                message,
                BeforeSha: parentBefore,
                AfterSha: parentBefore,
                Submodules: entries,
                Target: target,
                ParentExecuted: false,
                ParentPointerPending: pending);
        }

        var parent = await ExecuteCommitPlanAsync(parentPlan!, progress, cancellation);
        var parentPending = parent.Outcome is not CommitOutcome.Success
                            && await HasPendingParentPointerAsync(
                                worktree.WorktreePath, links, cancellation);
        return parent with
        {
            Submodules = entries,
            PartialCompletion = parent.Outcome is not CommitOutcome.Success and not CommitOutcome.Skipped
                                && entries.Any(item => item.Outcome == SubmoduleOperationOutcome.Success),
            Target = target,
            ParentExecuted = true,
            ParentPointerPending = parentPending,
        };
    }

    private async Task<bool> HasPendingParentPointerAsync(
        string parentPath,
        IEnumerable<GitlinkDescriptor> links,
        CancellationToken cancellation)
    {
        foreach (var link in links)
        {
            var childHead = await GitRunner.RunAsync(link.FullPath, ["rev-parse", "HEAD"],
                cancellation: cancellation);
            if (!childHead.Success)
                return true;
            var recorded = await _gitlinks.GetHeadGitlinkShaAsync(
                parentPath, link.RelativePath, cancellation);
            if (!recorded.Success || !recorded.Sha.Equals(
                    FirstLine(childHead.Output), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static async Task<string> ReadHeadShaAsync(
        string repository, CancellationToken cancellation)
    {
        var result = await GitRunner.RunAsync(repository, ["rev-parse", "HEAD"],
            cancellation: cancellation);
        return result.Success ? FirstLine(result.Output) : string.Empty;
    }

    private async Task<(RepositoryCommitPlan? Plan, CommitReport? Error)> PrepareCommitPlanAsync(
        WorktreeInfo worktree,
        string message,
        IProgress<string>? progress,
        IEnumerable<string> excludedDirectories,
        bool addAll,
        CancellationToken cancellation)
    {
        var name = await GitRunner.RunAsync(worktree.WorktreePath, ["config", "--get", "user.name"],
            cancellation: cancellation);
        var email = await GitRunner.RunAsync(worktree.WorktreePath, ["config", "--get", "user.email"],
            cancellation: cancellation);
        if (!name.Success || string.IsNullOrWhiteSpace(FirstLine(name.Output))
                          || !email.Success || string.IsNullOrWhiteSpace(FirstLine(email.Output)))
            return (null, new CommitReport(CommitOutcome.Failed,
                $"仓库缺少提交身份 user.name/user.email: {worktree.BranchName}"));

        var head = await GitRunner.RunAsync(worktree.WorktreePath, ["rev-parse", "HEAD"],
            cancellation: cancellation);
        if (!head.Success)
            return (null, new CommitReport(CommitOutcome.Failed,
                $"无法读取仓库 HEAD [{worktree.BranchName}]:\n{head.Output}"));

        progress?.Report($"[{worktree.BranchName}] 扫描文件大小...");
        var warnBytes = WarnBytes;
        var rejectBytes = RejectBytes;
        var (checkStatus, largeFiles) = await Task.Run(() =>
            WorktreeFileScanner.ScanDirectory(worktree.WorktreePath, warnBytes, rejectBytes,
                excludedDirectories), cancellation);
        var oversized = largeFiles.Where(file => file.SizeBytes >= rejectBytes).ToList();
        var needsLfs = new List<LargeFileEntry>();
        if (oversized.Count > 0)
        {
            if (!await WorktreeLfsHelper.IsGitLfsAvailableAsync(worktree.WorktreePath))
                return (null, new CommitReport(CommitOutcome.Failed,
                    $"发现 {oversized.Count} 个 ≥{rejectBytes / 1024 / 1024}MB 文件,但未检测到 Git LFS,无法处理"));
            foreach (var file in oversized)
            {
                if (!await WorktreeLfsHelper.IsFileManagedByLfsAsync(worktree.WorktreePath, file.RelativePath))
                    needsLfs.Add(file);
                else
                    progress?.Report($"   [LFS] {file.RelativePath}({file.FormattedSize})已用指针,放行");
            }
        }

        if (needsLfs.Count > 0 && !_confirm(BuildLfsPrompt(worktree.BranchName, needsLfs)))
        {
            return (null, new CommitReport(CommitOutcome.Rejected,
                $"用户拒绝启用 LFS,已跳过项目 [{worktree.BranchName}]", RejectedFiles: needsLfs));
        }

        if (checkStatus == FileSizeCheckStatus.Warning)
        {
            progress?.Report($"[{worktree.BranchName}] ⚠ {largeFiles.Count} 个文件 ≥ {warnBytes / 1024 / 1024}MB,仍继续提交:");
            foreach (var file in largeFiles)
                progress?.Report($"   [警告] {file.RelativePath}({file.FormattedSize})");
        }

        return (new RepositoryCommitPlan(worktree, message, checkStatus, largeFiles, needsLfs,
            FirstLine(head.Output), addAll), null);
    }

    private async Task<CommitReport> ExecuteCommitPlanAsync(
        RepositoryCommitPlan plan, IProgress<string>? progress, CancellationToken cancellation)
    {
        if (plan.NeedsLfs.Count > 0)
        {
            progress?.Report($"[{plan.Worktree.BranchName}] 配置 Git LFS...");
            var setup = await WorktreeLfsHelper.SetupLfsForFilesAsync(plan.Worktree.WorktreePath,
                plan.NeedsLfs.Select(file => file.RelativePath));
            if (!setup.Success)
                return new CommitReport(CommitOutcome.Failed, $"Git LFS 配置失败:\n{setup.Message}",
                    BeforeSha: plan.BeforeSha, AfterSha: plan.BeforeSha);
        }

        var addResult = await GitRunner.RunAsync(plan.Worktree.WorktreePath,
            plan.AddAll ? ["add", "-A"] : ["add", "."], cancellation: cancellation);
        if (!addResult.Success)
            return new CommitReport(CommitOutcome.Failed, $"git add 失败:\n{addResult.Output}",
                BeforeSha: plan.BeforeSha, AfterSha: plan.BeforeSha);

        var staged = await GitRunner.RunAsync(plan.Worktree.WorktreePath,
            ["diff", "--cached", "--quiet"], cancellation: cancellation);
        if (staged.ExitCode == 0)
            return new CommitReport(CommitOutcome.Skipped, "无变更,已跳过",
                plan.CheckStatus == FileSizeCheckStatus.Warning,
                BeforeSha: plan.BeforeSha, AfterSha: plan.BeforeSha);
        if (staged.ExitCode != 1)
            return new CommitReport(CommitOutcome.Failed, $"检查暂存区失败:\n{staged.Output}",
                BeforeSha: plan.BeforeSha, AfterSha: plan.BeforeSha);

        var commitResult = await GitRunner.RunAsync(plan.Worktree.WorktreePath,
            ["commit", "-m", plan.Message], cancellation: cancellation);
        if (!commitResult.Success)
        {
            if (commitResult.Output.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase)
                || commitResult.Output.Contains("无文件要提交", StringComparison.OrdinalIgnoreCase))
                return new CommitReport(CommitOutcome.Skipped, "无变更,已跳过",
                    plan.CheckStatus == FileSizeCheckStatus.Warning,
                    BeforeSha: plan.BeforeSha, AfterSha: plan.BeforeSha);
            return new CommitReport(CommitOutcome.Failed, $"git commit 失败:\n{commitResult.Output}",
                BeforeSha: plan.BeforeSha, AfterSha: plan.BeforeSha);
        }

        var after = await GitRunner.RunAsync(plan.Worktree.WorktreePath, ["rev-parse", "HEAD"],
            cancellation: cancellation);
        var afterSha = after.Success ? FirstLine(after.Output) : string.Empty;
        var summary = FirstLine(commitResult.Output);
        return new CommitReport(CommitOutcome.Success, $"已提交到本地仓库 {summary}".Trim(),
            plan.CheckStatus == FileSizeCheckStatus.Warning,
            BeforeSha: plan.BeforeSha, AfterSha: afterSha);
    }

    // ---------------------------------------------------------------- 推送

    public async Task<PushReport> PushAsync(
        string name, bool includeSubmodules = false, CancellationToken cancellation = default)
        => await PushAsync(name,
            includeSubmodules ? RepositoryTarget.Both : RepositoryTarget.Parent, cancellation);

    public async Task<PushReport> PushAsync(
        string name, RepositoryTarget target, CancellationToken cancellation = default)
    {
        name = name.Trim();
        var (resolved, resolveMessage, worktree) = await ResolveWorktreeAsync(name);
        if (!resolved || worktree == null)
            return new PushReport(false, resolveMessage, Target: target);
        var worktreePath = worktree.WorktreePath;

        var entries = new List<SubmoduleOperationEntry>();
        var pendingParentCount = 0;
        if (target != RepositoryTarget.Parent)
        {
            var prepared = await PrepareSubmodulePushesAsync(
                [(worktreePath, worktreePath)], target == RepositoryTarget.Both, cancellation);
            pendingParentCount = prepared.ParentPointerPendingCount;
            if (!prepared.Success)
                return new PushReport(false, prepared.Message, Submodules: prepared.Entries,
                    Target: target, ParentPointerPending: pendingParentCount > 0);
            var pushed = await PushSubmodulesAsync(prepared.Items, cancellation);
            entries = pushed.Entries;
            if (!pushed.Success)
                return new PushReport(false, pushed.Message, Submodules: entries,
                    PartialCompletion: entries.Any(item => item.Pushed), Target: target,
                    ParentPointerPending: pendingParentCount > 0);

            if (target == RepositoryTarget.Submodules)
            {
                var message = entries.Count == 0
                    ? "未发现直属子模块，已跳过"
                    : $"已推送 {entries.Count} 个子模块，父分支未推送" +
                      (pendingParentCount > 0 ? "；父 gitlink 尚待收口" : string.Empty);
                return new PushReport(true, message, ParentPushed: false, Submodules: entries,
                    Target: target, ParentPointerPending: pendingParentCount > 0);
            }
        }

        var result = await GitRunner.RunAsync(worktreePath, ["push", "origin", "HEAD"],
            cancellation: cancellation);
        return result.Success
            ? new PushReport(true, $"已推送到 origin (HEAD)\n{result.Output}".Trim(),
                true, entries, Target: target)
            : new PushReport(false, $"父仓库推送失败(退出码 {result.ExitCode}):\n{result.Output}",
                Submodules: entries, PartialCompletion: entries.Any(item => item.Pushed),
                Target: target, ParentPointerPending: pendingParentCount > 0);
    }

    public async Task<BatchPushReport> PushAllAsync(
        bool includeSubmodules = false, CancellationToken cancellation = default)
        => await PushAllAsync(
            includeSubmodules ? RepositoryTarget.Both : RepositoryTarget.Parent, cancellation);

    public async Task<BatchPushReport> PushAllAsync(
        RepositoryTarget target, CancellationToken cancellation = default)
    {
        var (listResult, worktrees) = await ListWorktreesAsync();
        if (!listResult.Success)
            return new BatchPushReport(false, $"获取项目列表失败:\n{listResult.Output}",
                false, [], Target: target);

        var entries = new List<SubmoduleOperationEntry>();
        var pendingParentCount = 0;
        if (target != RepositoryTarget.Parent)
        {
            var parents = worktrees.Where(item => Directory.Exists(item.WorktreePath))
                .Select(item => (item.WorktreePath, item.WorktreePath));
            var prepared = await PrepareSubmodulePushesAsync(
                parents, target == RepositoryTarget.Both, cancellation);
            pendingParentCount = prepared.ParentPointerPendingCount;
            if (!prepared.Success)
                return new BatchPushReport(false, prepared.Message, false, prepared.Entries,
                    Target: target, ParentPointerPendingCount: pendingParentCount);
            var submodulePush = await PushSubmodulesAsync(prepared.Items, cancellation);
            entries = submodulePush.Entries;
            if (!submodulePush.Success)
                return new BatchPushReport(false, submodulePush.Message, false, entries,
                    entries.Any(item => item.Pushed), target, pendingParentCount);

            if (target == RepositoryTarget.Submodules)
            {
                var message = entries.Count == 0
                    ? "全部工作树均未发现直属子模块，已跳过"
                    : $"已推送 {entries.Count} 个子模块，全部父分支未推送" +
                      (pendingParentCount > 0 ? $"；{pendingParentCount} 个父项目 gitlink 尚待收口" : string.Empty);
                return new BatchPushReport(true, message, false, entries,
                    Target: target, ParentPointerPendingCount: pendingParentCount);
            }
        }

        var pushed = 0;
        var failed = new List<string>();
        foreach (var item in worktrees.Where(item => Directory.Exists(item.WorktreePath)))
        {
            var result = await GitRunner.RunAsync(item.WorktreePath, ["push", "origin", "HEAD"],
                cancellation: cancellation);
            if (result.Success)
                pushed++;
            else
                failed.Add($"{item.BranchName}: {result.Output.Trim()}");
        }

        if (failed.Count == 0)
        {
            return new BatchPushReport(true, $"已推送 {pushed} 个项目仓的 HEAD 到 origin",
                true, entries, Target: target, ParentPointerPendingCount: pendingParentCount);
        }

        return new BatchPushReport(false,
            $"推送完成 {pushed} 个，失败 {failed.Count}:\n  - " + string.Join("\n  - ", failed),
            pushed > 0, entries, pushed > 0 || entries.Any(item => item.Pushed), target, pendingParentCount);
    }

    private async Task<(bool Success, string Message,
        List<(string ParentPath, GitlinkDescriptor Link)> Items,
        List<SubmoduleOperationEntry> Entries,
        int ParentPointerPendingCount)> PrepareSubmodulePushesAsync(
        IEnumerable<(string ParentPath, string Identity)> parents,
        bool requireParentPointerMatch,
        CancellationToken cancellation)
    {
        var items = new List<(string ParentPath, GitlinkDescriptor Link)>();
        var entries = new List<SubmoduleOperationEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (parentPath, _) in parents)
        {
            var discovered = await _gitlinks.DiscoverAsync(parentPath, cancellation);
            if (!discovered.Success)
                return (false, discovered.Message, [], entries, pendingParents.Count);
            foreach (var link in discovered.Items)
            {
                if (!seen.Add(Path.GetFullPath(link.FullPath)))
                    continue;
                var failure = string.IsNullOrWhiteSpace(link.Branch)
                    ? "处于 detached HEAD"
                    : link.IsDirty
                        ? "工作树不干净，请先提交"
                        : !link.HasOrigin
                            ? "不存在 origin"
                            : null;
                if (failure != null)
                {
                    entries.Add(new SubmoduleOperationEntry(link.RelativePath, link.Branch,
                        link.HeadSha, link.HeadSha, SubmoduleOperationOutcome.Rejected, failure, link.Kind));
                    return (false, $"子模块推送预检失败 [{link.RelativePath}]: {failure}",
                        [], entries, pendingParents.Count);
                }

                var recorded = await _gitlinks.GetHeadGitlinkShaAsync(parentPath, link.RelativePath,
                    cancellation);
                var pointerMatches = recorded.Success
                                     && recorded.Sha.Equals(link.HeadSha, StringComparison.OrdinalIgnoreCase);
                if (!pointerMatches)
                {
                    pendingParents.Add(parentPath);
                    var message = recorded.Success
                        ? $"父 HEAD 记录 {recorded.Sha[..Math.Min(12, recorded.Sha.Length)]}，子 HEAD 为 {link.HeadSha[..Math.Min(12, link.HeadSha.Length)]}"
                        : recorded.Message;
                    if (requireParentPointerMatch)
                    {
                        entries.Add(new SubmoduleOperationEntry(link.RelativePath, link.Branch,
                            link.HeadSha, link.HeadSha, SubmoduleOperationOutcome.Rejected, message, link.Kind));
                        return (false, $"子模块指针尚未由父项目提交 [{link.RelativePath}]: {message}",
                            [], entries, pendingParents.Count);
                    }
                }
                items.Add((parentPath, link));
            }
        }
        return (true, string.Empty,
            items.OrderBy(item => item.Link.FullPath, StringComparer.OrdinalIgnoreCase).ToList(),
            entries, pendingParents.Count);
    }

    private static async Task<(bool Success, string Message, List<SubmoduleOperationEntry> Entries)>
        PushSubmodulesAsync(
            IEnumerable<(string ParentPath, GitlinkDescriptor Link)> items,
            CancellationToken cancellation)
    {
        var entries = new List<SubmoduleOperationEntry>();
        foreach (var (_, link) in items)
        {
            var result = await GitRunner.RunAsync(link.FullPath,
                ["push", "origin", link.Branch], cancellation: cancellation);
            var entry = new SubmoduleOperationEntry(link.RelativePath, link.Branch,
                link.HeadSha, link.HeadSha,
                result.Success ? SubmoduleOperationOutcome.Success : SubmoduleOperationOutcome.Failed,
                result.Success ? result.Output : $"退出码 {result.ExitCode}: {result.Output}",
                link.Kind, result.Success);
            entries.Add(entry);
            if (!result.Success)
                return (false, $"子模块推送失败 [{link.RelativePath}]:\n{result.Output}", entries);
        }
        return (true, entries.Count == 0 ? "没有子模块需要推送" : $"已推送 {entries.Count} 个子模块", entries);
    }

    // ---------------------------------------------------------------- 批量提交

    public async Task<BatchCommitReport> CommitAllAsync(
        string message,
        IProgress<string>? progress,
        Action<string, CommitReport>? onProjectDone = null,
        bool includeSubmodules = false,
        string? submoduleMessage = null,
        CancellationToken cancellation = default)
        => await CommitAllAsync(message, progress, onProjectDone,
            includeSubmodules ? RepositoryTarget.Both : RepositoryTarget.Parent,
            submoduleMessage, cancellation);

    public async Task<BatchCommitReport> CommitAllAsync(
        string message,
        IProgress<string>? progress,
        Action<string, CommitReport>? onProjectDone,
        RepositoryTarget target,
        string? submoduleMessage = null,
        CancellationToken cancellation = default)
    {
        var (listResult, worktrees) = await ListWorktreesAsync();
        if (!listResult.Success)
            return new BatchCommitReport(false, $"获取工作树列表失败:\n{listResult.Output}", [],
                Target: target);
        worktrees = worktrees.Where(worktree => Directory.Exists(worktree.WorktreePath)).ToList();
        if (worktrees.Count == 0)
            return new BatchCommitReport(false, "未发现可提交的工作树", [], Target: target);

        var results = new List<ProjectCommitResult>();
        for (var i = 0; i < worktrees.Count; i++)
        {
            var worktree = worktrees[i];
            progress?.Report($"[{i + 1}/{worktrees.Count}] {worktree.BranchName} ...");
            var report = await CommitWorktreeAsync(worktree, message, progress,
                target, submoduleMessage, cancellation);
            results.Add(new ProjectCommitResult(worktree.BranchName, report));
            onProjectDone?.Invoke(worktree.BranchName, report);
            progress?.Report($"[{i + 1}/{worktrees.Count}] {worktree.BranchName} " +
                             (report.Outcome is CommitOutcome.Success or CommitOutcome.Skipped ? "✓ " : "✗ ") +
                             report.Message);
        }

        var success = results.Count(item => item.Report.Outcome == CommitOutcome.Success);
        var skipped = results.Count(item => item.Report.Outcome == CommitOutcome.Skipped);
        var rejected = results.Count(item => item.Report.Outcome == CommitOutcome.Rejected);
        var failed = results.Count(item => item.Report.Outcome == CommitOutcome.Failed);
        var summary = $"批量提交完成: 成功 {success} | 无变更跳过 {skipped} | 拒绝 {rejected} | 失败 {failed}";
        var failedNames = results.Where(item => item.Report.Outcome is CommitOutcome.Failed or CommitOutcome.Rejected)
            .Select(item => item.Project).ToList();
        if (failedNames.Count > 0)
            summary += $"\n失败/拒绝项目: {string.Join(", ", failedNames)}";
        var pendingCount = results.Count(item => item.Report.ParentPointerPending);
        if (pendingCount > 0)
            summary += $"\n待收口父项目: {pendingCount}";
        return new BatchCommitReport(failed == 0 && rejected == 0, summary, results,
            results.Any(item => item.Report.PartialCompletion)
            || success + skipped > 0 && failed + rejected > 0,
            target, pendingCount);
    }

    private static string BuildLfsPrompt(string name, IEnumerable<LargeFileEntry> files)
    {
        var list = files.ToList();
        var prompt = new StringBuilder();
        prompt.AppendLine($"项目【{name}】发现 {list.Count} 个超限文件尚未使用 Git LFS 指针:");
        foreach (var file in list)
            prompt.AppendLine($"  • {file.RelativePath}({file.FormattedSize})");
        prompt.Append("是否启用 Git LFS 后继续?选择「否」将中止该项目且不创建提交。");
        return prompt.ToString();
    }

    private static string BuildSubmoduleCommitPrompt(
        string parent,
        IEnumerable<(GitlinkDescriptor Link, RepositoryCommitPlan Plan)> plans,
        RepositoryTarget target)
    {
        var list = plans.ToList();
        var prompt = new StringBuilder();
        prompt.AppendLine($"项目【{parent}】将联动提交 {list.Count} 个子模块:");
        foreach (var (link, plan) in list)
            prompt.AppendLine($"  • {link.RelativePath} [{link.Branch}] 修改 {link.ModifiedCount} / 删除 {link.DeletedCount} / 未跟踪 {link.UntrackedCount}\n    描述: {plan.Message}");
        prompt.AppendLine();
        prompt.Append(target == RepositoryTarget.Submodules
            ? "本次只提交子模块，父分支将保留待收口 gitlink。多仓库无法原子回退，是否继续?"
            : "执行顺序固定为子模块先提交、父项目后提交。多仓库无法原子回退，是否继续?");
        return prompt.ToString();
    }

    private static IReadOnlyList<SubmoduleOperationEntry> BuildSkippedEntries(
        IEnumerable<GitlinkDescriptor> links)
        => links.Select(link => new SubmoduleOperationEntry(link.RelativePath, link.Branch,
            link.HeadSha, link.HeadSha, SubmoduleOperationOutcome.Skipped,
            link.IsDirty ? "尚未执行" : "工作树干净，已跳过", link.Kind)).ToList();

    private static void ReplaceEntry(
        List<SubmoduleOperationEntry> entries, SubmoduleOperationEntry replacement)
    {
        var index = entries.FindIndex(item => item.RelativePath.Equals(
            replacement.RelativePath, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            entries[index] = replacement;
        else
            entries.Add(replacement);
    }

    private static SubmoduleOperationOutcome ToSubmoduleOutcome(CommitOutcome outcome) => outcome switch
    {
        CommitOutcome.Success => SubmoduleOperationOutcome.Success,
        CommitOutcome.Skipped => SubmoduleOperationOutcome.Skipped,
        CommitOutcome.Rejected => SubmoduleOperationOutcome.Rejected,
        _ => SubmoduleOperationOutcome.Failed,
    };

    private static string FirstLine(string value)
        => value.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;

}
