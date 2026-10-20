using System.IO;
using HistoryJanus.GitHub;

namespace HistoryJanus.Git;

/// <summary>
/// ProjectService 的推送切面。仓库还没有 origin 时按需建远端，再 remote add 后推送。
/// 一个项目只推自己这一份仓库。
/// </summary>
public sealed partial class ProjectService
{
    // ---------------------------------------------------------------- 推送

    public async Task<PushReport> PushAsync(
        string name, bool includeSubmodules = false, CancellationToken cancellation = default)
        => await PushAsync(name, RepositoryTarget.Parent, cancellation: cancellation);

    public async Task<PushReport> PushAsync(
        string name,
        RepositoryTarget target,
        string? visibility = null,
        CancellationToken cancellation = default,
        IProgress<string>? progress = null)
    {
        name = name.Trim();
        var (resolved, resolveMessage, worktree) = await ResolveWorktreeAsync(name);
        if (!resolved || worktree == null)
            return new PushReport(false, resolveMessage, Target: target);
        var worktreePath = worktree.WorktreePath;
        var remote = await EnsureRemoteAsync(
            worktreePath, worktree.FolderName, visibility, cancellation);
        if (!remote.Success)
            return new PushReport(false, remote.Message, Target: target);

        var created = remote.Creation;
        var result = await PushParentAsync(
            worktreePath, setUpstream: created != null, progress, cancellation);

        // 建仓的结果必须同时出现在成功和失败两条消息里：推送失败时用户只看到 git 的报错，
        // 不说清楚就不知道 GitHub 上已经多了一个仓库。
        var prefix = created == null
            ? string.Empty
            : (created.Created ? $"已新建远端仓库 {created.FullName}（{created.Visibility}）；"
                : $"已复用既有远端仓库 {created.FullName}（{created.Visibility}）；");
        if (!result.Success)
            return new PushReport(false,
                $"{prefix}推送失败(退出码 {result.ExitCode}):\n{result.Output}",
                Target: target,
                RemoteCreated: created?.Created ?? false,
                RemoteUrl: created?.OriginUrl ?? string.Empty,
                RemoteVisibility: created?.Visibility ?? string.Empty);

        return new PushReport(true, $"{prefix}已推送到 origin\n{result.Output}".Trim(),
            true, Target: target,
            RemoteCreated: created?.Created ?? false,
            RemoteUrl: created?.OriginUrl ?? string.Empty,
            RemoteVisibility: created?.Visibility ?? string.Empty);
    }

    /// <summary>
    /// 父仓库推送（5.15.0）：被远端以「非快进」拒收时，取回该分支、交
    /// <see cref="DivergenceReconciler"/> 检查并收口，收口成功再推一次。
    /// 只认非快进拒收；传输中断、pre-receive 拒收等照旧原样报出——那些不是分叉。
    /// </summary>
    private async Task<GitResult> PushParentAsync(
        string worktreePath,
        bool setUpstream,
        IProgress<string>? progress,
        CancellationToken cancellation)
    {
        var first = await PushBranchAsync(worktreePath, branch: null, setUpstream, progress, cancellation);
        if (first.Success || !IsNonFastForwardRejection(first.Output))
            return first;

        var head = await GitRunner.RunAsync(worktreePath,
            ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellation);
        var branch = head.Success ? FirstLine(head.Output) : string.Empty;
        if (branch.Length == 0)
            return first;
        progress?.Report($"[{branch}] 推送被拒（远端有本地没有的提交），取回远端检查分叉...");
        // 只取这一条分支：这里要的是比较对象，不是全量刷新。
        var fetch = await GitRunner.RunAsync(worktreePath,
            ["fetch", "--no-tags", "origin", $"+refs/heads/{branch}:refs/remotes/origin/{branch}"],
            cancellation);
        if (!fetch.Success)
            return new GitResult(first.ExitCode, $"推送被拒且取回远端失败:\n{fetch.Output}\n{first.Output}");

        var reconciled = await DivergenceReconciler.ReconcileAsync(
            worktreePath, branch, DivergenceAdvisor, progress, cancellation);
        if (!reconciled.Changed)
            return new GitResult(first.ExitCode, reconciled.Outcome == ReconcileOutcome.NotDiverged
                ? first.Output
                : reconciled.Message);

        var second = await PushBranchAsync(worktreePath, branch, setUpstream, progress, cancellation);
        return second.Success
            ? new GitResult(0, $"{reconciled.Message}\n{second.Output}")
            : new GitResult(second.ExitCode, $"{reconciled.Message}\n但随后推送失败:\n{second.Output}");
    }

    /// <summary>
    /// git 对「远端有本地没有的提交」的拒收：<c>! [rejected] main -> main (fetch first)</c>
    /// 或 <c>(non-fast-forward)</c>。括号里的原因会随 git 界面语言翻译，<c>[rejected]</c> 标记不会；
    /// <c>[remote rejected]</c> 是服务端钩子拒收，不是分叉。
    /// </summary>
    internal static bool IsNonFastForwardRejection(string output)
        => output.Contains("(fetch first)", StringComparison.Ordinal)
           || output.Contains("(non-fast-forward)", StringComparison.Ordinal)
           || (output.Contains("[rejected]", StringComparison.Ordinal)
               && !output.Contains("[remote rejected]", StringComparison.Ordinal));

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

        var plan = await GitRunner.RunAsync(worktreePath, ["config", "--local", "--get", KeyPlannedRemote], cancellation);
        var plannedUrl = plan.Success ? FirstLine(plan.Output) : "";
        var info = GitHubConnectionService.ParseRemote(plannedUrl, plannedUrl);
        var githubPlan = plannedUrl.Length > 0 && info.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);
        if (plannedUrl.Length == 0 || githubPlan)
        {
            if (RepositoryProvisioner == null)
                return (false, $"仓库未配置 origin，且未启用远端自动创建: {projectName}", null);
        }
        if (githubPlan)
        {
            var path = Uri.TryCreate(plannedUrl, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
                ? uri.AbsolutePath.Trim('/') : plannedUrl[(plannedUrl.IndexOf(':') + 1)..].Trim('/');
            if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
            if (!path.Equals($"{info.Owner}/{info.Repository}", StringComparison.Ordinal))
                return (false, "预填远端必须是完整的 GitHub 仓库地址（owner/repository）", null);
        }
        var remoteName = githubPlan ? $"{info.Owner}/{info.Repository}" : ProjectRepoLayout.ToRemoteRepositoryName(projectName);
        GitHubRepositoryCreation creation;
        try
        {
            creation = plannedUrl.Length > 0 && !githubPlan
                ? new GitHubRepositoryCreation(info.Repository, plannedUrl, plannedUrl, plannedUrl, "", false, "")
                : await RepositoryProvisioner!.EnsureAsync(worktreePath, remoteName,
                    GitHubRepositoryProvisioner.NormalizeVisibility(visibility), cancellation);
            if (plannedUrl.Length > 0) creation = creation with { ConfiguredUrl = plannedUrl };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return (false, $"创建远端仓库失败 [{projectName} → {remoteName}]: {ex.Message}", null);
        }

        var added = await GitRunner.RunAsync(worktreePath,
            ["remote", "add", "origin", creation.OriginUrl], cancellation: cancellation);
        if (added.Success && plannedUrl.Length > 0)
            await GitRunner.RunAsync(worktreePath, ["config", "--local", "--unset-all", KeyPlannedRemote], cancellation);
        return added.Success
            ? (true, string.Empty, creation)
            : (false, $"远端仓库已就绪 {creation.FullName}，但配置 origin 失败:\n{added.Output}", null);
    }

    public async Task<BatchPushReport> PushAllAsync(
        bool includeSubmodules = false, CancellationToken cancellation = default)
        => await PushAllAsync(RepositoryTarget.Parent, cancellation: cancellation);

    public async Task<BatchPushReport> PushAllAsync(
        RepositoryTarget target,
        string? visibility = null,
        CancellationToken cancellation = default,
        IProgress<string>? progress = null)
    {
        var (listResult, worktrees) = await ListWorktreesAsync();
        if (!listResult.Success)
            return new BatchPushReport(false, $"获取项目列表失败:\n{listResult.Output}",
                false, [], Target: target);

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

            var result = await PushParentAsync(item.WorktreePath,
                setUpstream: remote.Creation != null, progress, cancellation);
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
                $"已推送 {pushed} 个项目的 HEAD 到 origin{remoteSuffix}",
                true, [], Target: target, CreatedRemotes: createdRemotes);
        }

        return new BatchPushReport(false,
            $"推送完成 {pushed} 个，失败 {failed.Count}:\n  - " + string.Join("\n  - ", failed) + remoteSuffix,
            pushed > 0, [], PartialCompletion: pushed > 0, Target: target,
            CreatedRemotes: createdRemotes);
    }
}
