using System.IO;
using HistoryJanus.GitHub;

namespace HistoryJanus.Git;

/// <summary>
/// ProjectService 的推送切面。与提交切面共享 gitlink 发现和工作树解析，
/// 但多了一条提交没有的分支：仓库还没有 origin 时按需建远端，再 remote add 后推送。
/// </summary>
public sealed partial class ProjectService
{
    // ---------------------------------------------------------------- 推送

    public async Task<PushReport> PushAsync(
        string name, bool includeSubmodules = false, CancellationToken cancellation = default)
        => await PushAsync(name,
            includeSubmodules ? RepositoryTarget.Both : RepositoryTarget.Parent,
            cancellation: cancellation);

    public async Task<PushReport> PushAsync(
        string name,
        RepositoryTarget target,
        string? visibility = null,
        CancellationToken cancellation = default)
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

        var remote = await EnsureRemoteAsync(
            worktreePath, worktree.FolderName, visibility, cancellation);
        if (!remote.Success)
            return new PushReport(false, remote.Message, Submodules: entries,
                PartialCompletion: entries.Any(item => item.Pushed), Target: target,
                ParentPointerPending: pendingParentCount > 0);

        var created = remote.Creation;
        var result = await PushBranchAsync(
            worktreePath, branch: null, setUpstream: created != null, progress: null, cancellation);

        // 建仓的结果必须同时出现在成功和失败两条消息里：推送失败时用户只看到 git 的报错，
        // 不说清楚就不知道 GitHub 上已经多了一个仓库。
        var prefix = created == null
            ? string.Empty
            : (created.Created ? $"已新建远端仓库 {created.FullName}（{created.Visibility}）；"
                : $"已复用既有远端仓库 {created.FullName}（{created.Visibility}）；");
        if (!result.Success)
            return new PushReport(false,
                $"{prefix}父仓库推送失败(退出码 {result.ExitCode}):\n{result.Output}",
                Submodules: entries, PartialCompletion: entries.Any(item => item.Pushed),
                Target: target, ParentPointerPending: pendingParentCount > 0,
                RemoteCreated: created?.Created ?? false,
                RemoteUrl: created?.OriginUrl ?? string.Empty,
                RemoteVisibility: created?.Visibility ?? string.Empty);

        return new PushReport(true, $"{prefix}已推送到 origin\n{result.Output}".Trim(),
            true, entries, Target: target,
            RemoteCreated: created?.Created ?? false,
            RemoteUrl: created?.OriginUrl ?? string.Empty,
            RemoteVisibility: created?.Visibility ?? string.Empty);
    }

    /// <summary>
    /// 全部父仓库推送的唯一出口。把 2026-08 全库首推里手工验证过的四条经验固化下来：
    ///
    /// 1. **推显式分支名，不推 HEAD**。`push origin HEAD` 在部分仓上报
    ///    「hint: 'HEAD:refs/heads/HEAD'?」而失败，换成分支名即通过。
    /// 2. **关掉 lfs.locksverify**。缺失 LFS 对象较多的仓在 locks/verify 端点撞 EOF，
    ///    整个推送失败；这个校验对本链路没有价值。
    /// 3. **超预算就按提交分批推**。单次推送超过约 300MB 在本机链路上会中断，
    ///    分批后同样的内容能稳定推完。
    /// 4. 失败消息保留 git 原文，便于区分传输中断和服务端拒收（pre-receive）。
    /// </summary>
    private static async Task<GitResult> PushBranchAsync(
        string worktreePath,
        string? branch,
        bool setUpstream,
        IProgress<string>? progress,
        CancellationToken cancellation)
    {
        // 分支名必须现读：WorktreeInfo.BranchName 是项目目录名（共享裸仓时代的遗留命名），
        // 不是 git 分支名，拿它去推会得到 "src refspec ... does not match any"。
        if (string.IsNullOrWhiteSpace(branch))
        {
            var head = await GitRunner.RunAsync(worktreePath,
                ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellation);
            branch = head.Success ? FirstLine(head.Output) : string.Empty;
        }
        if (string.IsNullOrWhiteSpace(branch))
            return new GitResult(1,
                "无法确定要推送的分支名：仓库处于 detached HEAD，请先 checkout 到分支");

        var batches = await PlanPushBatchesAsync(worktreePath, branch, cancellation);
        for (var i = 0; i < batches.Count; i++)
        {
            var last = i == batches.Count - 1;
            var refspec = last ? branch : $"{batches[i]}:refs/heads/{branch}";
            if (batches.Count > 1)
                progress?.Report($"[{branch}] 分批推送 {i + 1}/{batches.Count} ...");
            List<string> arguments = ["-c", "lfs.locksverify=false", "push"];
            if (setUpstream && last)
                arguments.Add("-u");
            arguments.Add("origin");
            arguments.Add(refspec);
            var result = await GitRunner.RunAsync(worktreePath, arguments, cancellation);
            if (!result.Success)
                return batches.Count > 1
                    ? new GitResult(result.ExitCode,
                        $"分批推送在第 {i + 1}/{batches.Count} 批失败（之前各批已推上去，重跑会从断点继续）:\n{result.Output}")
                    : result;
        }
        return new GitResult(0, batches.Count > 1
            ? $"已分 {batches.Count} 批推送 {branch}"
            : $"已推送 {branch}");
    }

    /// <summary>
    /// 规划推送批次：返回中间提交列表，最后一项恒为分支 tip（由调用方用分支名推）。
    /// 待推内容在预算内时返回单批，不引入任何额外 git 调用带来的开销。
    /// </summary>
    private static async Task<List<string>> PlanPushBatchesAsync(
        string worktreePath, string branch, CancellationToken cancellation)
    {
        var single = new List<string> { branch };
        // --disk-usage 直接给出待推字节数，比自己遍历对象可靠（git 2.31+）。
        var range = await GitRunner.RunAsync(worktreePath,
            ["rev-list", "--disk-usage", "--objects", branch, "--not", "--remotes=origin"],
            cancellation);
        if (!range.Success || !long.TryParse(FirstLine(range.Output), out var bytes)
                           || bytes <= PushChunkBudgetBytes)
            return single;

        var listed = await GitRunner.RunAsync(worktreePath,
            ["rev-list", "--reverse", branch, "--not", "--remotes=origin"], cancellation);
        if (!listed.Success)
            return single;
        var commits = listed.Output
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        if (commits.Count <= 1)
            return single; // 单个提交无法再切；只能整包推，失败由调用方原样报出

        // 按字节预算估算批数，再按提交数均分。均分不精确，但足以把单批压回预算量级，
        // 而逐提交量算 disk-usage 会带来 N 次 git 调用。
        var batchCount = (int)Math.Min(commits.Count,
            Math.Max(2, (bytes + PushChunkBudgetBytes - 1) / PushChunkBudgetBytes));
        var step = (commits.Count + batchCount - 1) / batchCount;
        var batches = new List<string>();
        for (var index = step - 1; index < commits.Count - 1; index += step)
            batches.Add(commits[index]);
        batches.Add(branch);
        return batches;
    }

    /// <summary>
    /// 推送前确保 origin 存在：已有则原样放行；缺失且配了 provisioner 时按需建远端再 remote add。
    /// provision 失败绝不写 remote，避免留下半配的仓库让下一次推送更难诊断。
    /// </summary>
    private async Task<(bool Success, string Message, GitHubRepositoryCreation? Creation)>
        EnsureRemoteAsync(
            string worktreePath,
            string projectName,
            string? visibility,
            CancellationToken cancellation)
    {
        var existing = await GitRunner.RunAsync(worktreePath, ["remote", "get-url", "origin"],
            cancellation: cancellation);
        if (existing.Success && !string.IsNullOrWhiteSpace(FirstLine(existing.Output)))
            return (true, string.Empty, null);

        if (RepositoryProvisioner == null)
            return (false, $"仓库未配置 origin，且未启用远端自动创建: {projectName}", null);

        // 本地目录名保留中文，远端只用编号：GitHub 会吃掉非 ASCII 字符。
        var remoteName = ProjectRepoLayout.ToRemoteRepositoryName(projectName);
        GitHubRepositoryCreation creation;
        try
        {
            creation = await RepositoryProvisioner.EnsureAsync(
                worktreePath, remoteName,
                GitHubRepositoryProvisioner.NormalizeVisibility(visibility), cancellation);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, $"创建远端仓库失败 [{projectName} → {remoteName}]: {ex.Message}", null);
        }

        var added = await GitRunner.RunAsync(worktreePath,
            ["remote", "add", "origin", creation.OriginUrl], cancellation: cancellation);
        return added.Success
            ? (true, string.Empty, creation)
            : (false, $"远端仓库已就绪 {creation.FullName}，但配置 origin 失败:\n{added.Output}", null);
    }

    public async Task<BatchPushReport> PushAllAsync(
        bool includeSubmodules = false, CancellationToken cancellation = default)
        => await PushAllAsync(
            includeSubmodules ? RepositoryTarget.Both : RepositoryTarget.Parent,
            cancellation: cancellation);

    public async Task<BatchPushReport> PushAllAsync(
        RepositoryTarget target,
        string? visibility = null,
        CancellationToken cancellation = default)
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
        var createdRemotes = new List<CreatedRemoteEntry>();
        foreach (var item in worktrees.Where(item => Directory.Exists(item.WorktreePath)))
        {
            var remote = await EnsureRemoteAsync(
                item.WorktreePath, item.FolderName, visibility, cancellation);
            if (!remote.Success)
            {
                failed.Add($"{item.BranchName}: {remote.Message}");
                continue;
            }
            if (remote.Creation != null)
                createdRemotes.Add(new CreatedRemoteEntry(item.BranchName,
                    remote.Creation.FullName, remote.Creation.OriginUrl,
                    remote.Creation.Visibility, remote.Creation.Created));

            var result = await PushBranchAsync(item.WorktreePath, branch: null,
                setUpstream: remote.Creation != null, progress: null, cancellation);
            if (result.Success)
                pushed++;
            else
                failed.Add($"{item.BranchName}: {result.Output.Trim()}");
        }

        var newRemotes = createdRemotes.Count(remote => remote.Created);
        var remoteSuffix = newRemotes == 0
            ? string.Empty
            : $"\n新建远端仓库 {newRemotes} 个:\n  + " + string.Join("\n  + ",
                createdRemotes.Where(remote => remote.Created)
                    .Select(remote => $"{remote.FullName}（{remote.Visibility}）"));

        if (failed.Count == 0)
        {
            return new BatchPushReport(true,
                $"已推送 {pushed} 个项目仓的 HEAD 到 origin{remoteSuffix}",
                true, entries, Target: target, ParentPointerPendingCount: pendingParentCount,
                CreatedRemotes: createdRemotes);
        }

        return new BatchPushReport(false,
            $"推送完成 {pushed} 个，失败 {failed.Count}:\n  - " + string.Join("\n  - ", failed) + remoteSuffix,
            pushed > 0, entries, pushed > 0 || entries.Any(item => item.Pushed), target,
            pendingParentCount, createdRemotes);
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
            var result = await PushBranchAsync(link.FullPath, link.Branch,
                setUpstream: false, progress: null, cancellation);
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
}
