namespace HistoryJanus.Git;

/// <summary>项目仓里的一条本地分支。</summary>
public sealed record BranchInfo(
    string Name,
    string ShortSha,
    string Time,
    string Subject,
    bool IsCurrent,
    string Track,
    bool MergedIntoMain = false);

public sealed record BranchListReport(string Project, string Current, IReadOnlyList<BranchInfo> Branches);

/// <param name="Dirty">因工作树不干净被拒绝；界面据此直接弹提交窗口。</param>
public sealed record BranchMutationResult(bool Success, string Message, bool Dirty = false);

/// <summary>
/// 项目仓内的分支四件事：分出、切换、合并到当前、删除（5.16.0）。
///
/// 一个项目只有一份工作树，「切换」就是把这份工作树换到另一条分支上。
/// 会动工作树的两件（切换、合并）都要求工作树干净：脏工作树被带过分支、或混进合并提交，
/// 事后都分不清哪些改动属于哪条分支。拒绝时带 <see cref="BranchMutationResult.Dirty"/>，
/// 由界面接着弹提交窗口，而不是替人决定这些改动的去处。
/// </summary>
public sealed class BranchService(ProjectService projects)
{
    public async Task<(bool Success, string Message, BranchListReport? Report)> ListAsync(
        string name, CancellationToken cancellation = default)
    {
        var repo = await ResolveRepoAsync(name);
        if (repo.Path == null)
            return (false, repo.Message, null);

        var current = await CurrentAsync(repo.Path, cancellation);
        var refs = await GitRunner.RunAsync(repo.Path,
            ["for-each-ref", "refs/heads",
                "--format=%(refname:short)%1f%(objectname:short)%1f%(committerdate:format:%Y-%m-%d %H:%M)%1f%(subject)%1f%(upstream:track,nobracket)"],
            cancellation);
        if (!refs.Success)
            return (false, $"读取分支失败:\n{refs.Output}", null);

        // 已并入 main 的分支（5.16.1）：分支控制行只摆没合并的，合并完的不再占位。
        var merged = await GitRunner.RunAsync(repo.Path,
            ["branch", "--merged", $"refs/heads/{ProjectService.MainlineBranch}", "--format=%(refname:short)"],
            cancellation);
        var mergedNames = merged.Success
            ? merged.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.Ordinal)
            : [];

        var branches = refs.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\u001f'))
            .Where(parts => parts.Length >= 4)
            .Select(parts => new BranchInfo(
                parts[0], parts[1], parts[2], parts[3],
                parts[0].Equals(current, StringComparison.Ordinal),
                parts.Length > 4 ? parts[4] : "",
                parts[0] != ProjectService.MainlineBranch && mergedNames.Contains(parts[0])))
            // 当前分支在最上，其次 main，其余按名字。
            .OrderByDescending(branch => branch.IsCurrent)
            .ThenByDescending(branch => branch.Name == ProjectService.MainlineBranch)
            .ThenBy(branch => branch.Name, StringComparer.Ordinal)
            .ToList();
        return (true, $"{name}：{branches.Count} 条本地分支，当前 {current}",
            new BranchListReport(name, current, branches));
    }

    /// <summary>从 <paramref name="from"/> 分出新分支，不切换过去。</summary>
    public async Task<BranchMutationResult> CreateAsync(
        string name, string from, string branch, CancellationToken cancellation = default)
    {
        var repo = await ResolveRepoAsync(name);
        if (repo.Path == null)
            return new(false, repo.Message);
        branch = branch.Trim();
        var valid = await GitRunner.RunAsync(repo.Path, ["check-ref-format", "--branch", branch], cancellation);
        if (branch.Length == 0 || !valid.Success)
            return new(false, $"分支名不合法：{branch}");
        if (await ExistsAsync(repo.Path, branch, cancellation))
            return new(false, $"分支已存在：{branch}");
        if (!await ExistsAsync(repo.Path, from, cancellation))
            return new(false, $"起点分支不存在：{from}");

        var created = await GitRunner.RunAsync(repo.Path, ["branch", "--no-track", branch, from], cancellation);
        return created.Success
            ? new(true, $"{name}：已从 {from} 分出 {branch}")
            : new(false, $"分出分支失败:\n{created.Output}");
    }

    /// <summary>把项目工作树切到 <paramref name="branch"/>；工作树不干净就拒绝。</summary>
    public async Task<BranchMutationResult> SwitchAsync(
        string name, string branch, CancellationToken cancellation = default)
    {
        var repo = await ResolveRepoAsync(name);
        if (repo.Path == null)
            return new(false, repo.Message);
        var current = await CurrentAsync(repo.Path, cancellation);
        if (branch.Equals(current, StringComparison.Ordinal))
            return new(true, $"{name}：已在 {branch} 上");
        if (!await ExistsAsync(repo.Path, branch, cancellation))
            return new(false, $"分支不存在：{branch}");
        if (await IsDirtyAsync(name, cancellation))
            return new(false, $"{name}：工作树有未提交内容，先提交再切换到 {branch}", Dirty: true);

        var switched = await GitRunner.RunAsync(repo.Path, ["switch", branch], cancellation);
        return switched.Success
            ? new(true, $"{name}：工作树已从 {current} 切换到 {branch}")
            : new(false, $"切换分支失败:\n{switched.Output}");
    }

    /// <summary>
    /// 把 <paramref name="branch"/> 合并进当前分支。有冲突就整个退回（<c>merge --abort</c>），
    /// 与分叉收口同一条规矩：有文本冲突就什么都不动。
    /// </summary>
    public async Task<BranchMutationResult> MergeAsync(
        string name, string branch, CancellationToken cancellation = default)
    {
        var repo = await ResolveRepoAsync(name);
        if (repo.Path == null)
            return new(false, repo.Message);
        var current = await CurrentAsync(repo.Path, cancellation);
        if (current.Length == 0)
            return new(false, $"{name}：HEAD 不在任何分支上，无法合并");
        if (branch.Equals(current, StringComparison.Ordinal))
            return new(false, $"{branch} 就是当前分支，不能合并到自己");
        if (!await ExistsAsync(repo.Path, branch, cancellation))
            return new(false, $"分支不存在：{branch}");
        if (await IsDirtyAsync(name, cancellation))
            return new(false, $"{name}：工作树有未提交内容，先提交再合并 {branch}", Dirty: true);

        var merged = await GitRunner.RunAsync(repo.Path,
            ["merge", "--no-edit", "-m", $"合并 {branch} 到 {current}", branch], cancellation);
        if (merged.Success)
            return new(true, $"{name}：已把 {branch} 合并到 {current}");
        // 不用调用方的取消令牌：合并做了一半时必须退回去。
        _ = await GitRunner.RunAsync(repo.Path, ["merge", "--abort"], CancellationToken.None);
        return new(false, $"合并 {branch} 到 {current} 失败，已退回合并前状态:\n{merged.Output}");
    }

    /// <summary>删除分支。当前分支与 main 不许删；未合并的要 <paramref name="force"/>。</summary>
    public async Task<BranchMutationResult> DeleteAsync(
        string name, string branch, bool force, CancellationToken cancellation = default)
    {
        var repo = await ResolveRepoAsync(name);
        if (repo.Path == null)
            return new(false, repo.Message);
        if (branch.Equals(ProjectService.MainlineBranch, StringComparison.Ordinal))
            return new(false, $"{ProjectService.MainlineBranch} 是项目主线，不能删除");
        if (branch.Equals(await CurrentAsync(repo.Path, cancellation), StringComparison.Ordinal))
            return new(false, $"{branch} 是当前分支，先切换到别的分支再删除");
        if (!await ExistsAsync(repo.Path, branch, cancellation))
            return new(false, $"分支不存在：{branch}");

        var deleted = await GitRunner.RunAsync(repo.Path, ["branch", force ? "-D" : "-d", branch], cancellation);
        return deleted.Success
            ? new(true, $"{name}：已删除分支 {branch}")
            : new(false, $"删除分支失败（未合并的分支要强制删除）:\n{deleted.Output}");
    }

    /// <summary>分支是否已完全并入当前分支。界面据此决定要不要再确认一次「强制删除」。</summary>
    public async Task<bool> IsMergedAsync(string name, string branch, CancellationToken cancellation = default)
    {
        var repo = await ResolveRepoAsync(name);
        if (repo.Path == null)
            return false;
        var check = await GitRunner.RunAsync(repo.Path,
            ["merge-base", "--is-ancestor", $"refs/heads/{branch}", "HEAD"], cancellation);
        return check.Success;
    }

    public async Task<string> CurrentBranchAsync(string name, CancellationToken cancellation = default)
    {
        var repo = await ResolveRepoAsync(name);
        return repo.Path == null ? "" : await CurrentAsync(repo.Path, cancellation);
    }

    private async Task<bool> IsDirtyAsync(string name, CancellationToken cancellation)
        => (await projects.ReadWorktreeDiffAsync(name, RepositoryTarget.Parent, cancellation)).IsDirty;

    private async Task<(string? Path, string Message)> ResolveRepoAsync(string name)
    {
        name = name.Trim();
        if (name.Length == 0)
            return (null, "项目名称不能为空");
        var resolved = await projects.ResolveWorktreeAsync(name);
        return resolved.Success && resolved.Worktree != null
            ? (resolved.Worktree.WorktreePath, "")
            : (null, resolved.Message);
    }

    private static async Task<string> CurrentAsync(string repo, CancellationToken cancellation)
    {
        var head = await GitRunner.RunAsync(repo, ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellation);
        return head.Success ? head.Output.Trim() : "";
    }

    private static async Task<bool> ExistsAsync(string repo, string branch, CancellationToken cancellation)
        => branch.Length > 0
           && (await GitRunner.RunAsync(repo, ["show-ref", "--verify", "--quiet", $"refs/heads/{branch}"], cancellation)).Success;
}
