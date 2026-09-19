using System.IO;
using System.Text;

namespace HistoryJanus.Git;

/// <summary>一个仓（父仓或某个子模块）的脏工作树读数。</summary>
public sealed record WorktreeDiffScope(
    string Label,
    int ChangedFiles,
    int UntrackedFiles,
    string Body);

/// <summary>
/// 一次提交**将要**带走的内容。提交前先把它摆给人看，不是给机器判断用的：
/// <see cref="Summary"/> 进弹窗标题行，<see cref="Body"/> 进正文。
/// </summary>
public sealed record WorktreeDiffReport(
    string Project,
    bool IsDirty,
    int ChangedFiles,
    int UntrackedFiles,
    string Summary,
    string Body,
    IReadOnlyList<WorktreeDiffScope> Scopes,
    RepositoryTarget Target = RepositoryTarget.Parent);

/// <summary>丢弃脏工作树的结果。按仓逐条记，回执要说清楚到底动了哪几个仓。</summary>
public sealed record DiscardReport(
    bool Success,
    string Message,
    IReadOnlyList<string> Repositories,
    RepositoryTarget Target = RepositoryTarget.Parent);

/// <summary>
/// ProjectService 的脏工作树切面：提交前的差异读数，以及「这次不提交了，全丢掉」。
///
/// 两者共用同一套范围解析（父仓 / 子模块 / 两者），因此放在一起：预览里看到几个仓，
/// 丢弃就动几个仓——两边各自解析范围的话，人看到的和实际删掉的迟早会对不上。
/// </summary>
public sealed partial class ProjectService
{
    /// <summary>
    /// 差异正文的字符上限。弹窗是给人读的，几十万字的正文既读不完也会让窗口卡住；
    /// 截断时明说截断，不悄悄给一份看上去完整的半截差异。
    /// </summary>
    public const int DiffBodyLimit = 200_000;

    /// <summary>
    /// 读一个项目当前脏工作树的差异。只读：不 add、不 stash、不碰索引。
    ///
    /// 比的是 <c>HEAD</c> 而不是索引：已暂存和未暂存的改动都会进这次提交，
    /// 只看未暂存的那一半会让预览少掉人上一步刚 add 进去的东西。
    /// </summary>
    public async Task<WorktreeDiffReport> ReadWorktreeDiffAsync(
        string name,
        RepositoryTarget target = RepositoryTarget.Parent,
        CancellationToken cancellation = default)
    {
        name = name.Trim();
        var (resolved, resolveMessage, worktree) = await ResolveWorktreeAsync(name);
        if (!resolved || worktree == null)
            return new WorktreeDiffReport(name, false, 0, 0, resolveMessage, "", [], target);

        var scopes = new List<WorktreeDiffScope>();
        foreach (var repository in await ResolveScopesAsync(worktree, target, cancellation))
        {
            var scope = await ReadScopeAsync(repository.Label, repository.Path, cancellation);
            if (scope.ChangedFiles > 0 || scope.UntrackedFiles > 0)
                scopes.Add(scope);
        }

        var changed = scopes.Sum(scope => scope.ChangedFiles);
        var untracked = scopes.Sum(scope => scope.UntrackedFiles);
        var summary = scopes.Count == 0
            ? "工作树干净，没有要提交的内容"
            : $"{name}：{changed} 个文件有改动、{untracked} 个未跟踪文件"
              + (scopes.Count > 1 ? $"，分布在 {scopes.Count} 个仓" : "");
        var body = Truncate(string.Join("\n", scopes.Select(scope => scope.Body)));
        return new WorktreeDiffReport(
            name, scopes.Count > 0, changed, untracked, summary, body, scopes, target);
    }

    /// <summary>
    /// 丢弃脏工作树：回到 HEAD 的干净状态，未跟踪的新文件一并删除。
    ///
    /// 不加 <c>-x</c>：被 .gitignore 排除的内容（bin/obj、venv、node_modules、
    /// 各仓 z 级快照的生成物）不是「本次的改动」，删掉它们只会让人重跑一次构建。
    /// 这一步不可撤销——未跟踪文件不进回收站——所以指令侧带确认。
    /// </summary>
    public async Task<DiscardReport> DiscardAsync(
        string name,
        RepositoryTarget target = RepositoryTarget.Parent,
        IProgress<string>? progress = null,
        CancellationToken cancellation = default)
    {
        name = name.Trim();
        var (resolved, resolveMessage, worktree) = await ResolveWorktreeAsync(name);
        if (!resolved || worktree == null)
            return new DiscardReport(false, resolveMessage, [], target);

        var touched = new List<string>();
        foreach (var repository in await ResolveScopesAsync(worktree, target, cancellation))
        {
            progress?.Report($"[{repository.Label}] 丢弃工作树改动...");
            var reset = await GitRunner.RunAsync(
                repository.Path, ["reset", "--hard", "HEAD"], cancellation);
            if (!reset.Success)
                return new DiscardReport(false,
                    $"回退已跟踪改动失败 [{repository.Label}]:\n{reset.Output}", touched, target);
            var clean = await GitRunner.RunAsync(
                repository.Path, ["clean", "-fd"], cancellation);
            if (!clean.Success)
                return new DiscardReport(false,
                    $"删除未跟踪文件失败 [{repository.Label}]:\n{clean.Output}", touched, target);
            touched.Add(repository.Label);
        }

        return touched.Count == 0
            ? new DiscardReport(true, "工作树本来就是干净的，没有可丢弃的内容", touched, target)
            : new DiscardReport(true,
                $"已丢弃 {touched.Count} 个仓的脏工作树，均回到各自 HEAD：{string.Join("、", touched)}",
                touched, target);
    }

    /// <summary>
    /// 本次范围里真正要处理的仓。父仓恒在（除非只要子模块），子模块只收脏的那些——
    /// 干净的子模块既没有可看的差异，也没有可丢弃的内容。
    /// </summary>
    private async Task<List<(string Label, string Path)>> ResolveScopesAsync(
        WorktreeInfo worktree,
        RepositoryTarget target,
        CancellationToken cancellation)
    {
        var scopes = new List<(string Label, string Path)>();
        if (target != RepositoryTarget.Parent)
        {
            var discovered = await _gitlinks.DiscoverAsync(worktree.WorktreePath, cancellation);
            if (discovered.Success)
            {
                scopes.AddRange(discovered.Items
                    .Where(link => link.IsDirty)
                    .Select(link => ($"{worktree.BranchName}/{link.RelativePath}", link.FullPath)));
            }
        }
        if (target != RepositoryTarget.Submodules)
            scopes.Add((worktree.BranchName, worktree.WorktreePath));
        return scopes;
    }

    private static async Task<WorktreeDiffScope> ReadScopeAsync(
        string label, string repository, CancellationToken cancellation)
    {
        var status = await GitRunner.RunAsync(
            repository, ["status", "--porcelain=v1", "--untracked-files=all"], cancellation);
        if (!status.Success)
            return new WorktreeDiffScope(label, 0, 0, $"### {label}\n读取工作树状态失败:\n{status.Output}");

        var untracked = status.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd())
            .Where(line => line.StartsWith("?? ", StringComparison.Ordinal))
            .Select(line => GitPath.Unquote(line[3..].Trim()))
            .ToList();

        var stat = await GitRunner.RunAsync(repository, ["diff", "--stat", "HEAD"], cancellation);
        var diff = await GitRunner.RunAsync(repository, ["diff", "HEAD"], cancellation);
        var changed = await CountChangedFilesAsync(repository, cancellation);

        var body = new StringBuilder();
        body.Append("### ").Append(label).Append('\n');
        if (stat.Success && stat.Output.Trim().Length > 0)
            body.Append(stat.Output.TrimEnd()).Append("\n\n");
        if (untracked.Count > 0)
        {
            body.Append($"未跟踪的新文件（{untracked.Count} 个，本次会一并提交）:\n");
            foreach (var file in untracked)
                body.Append("  + ").Append(file).Append('\n');
            body.Append('\n');
        }
        if (diff.Success && diff.Output.Trim().Length > 0)
            body.Append(diff.Output.TrimEnd()).Append('\n');
        else if (changed == 0 && untracked.Count > 0)
            body.Append("（本仓只有未跟踪的新文件，没有已跟踪文件的改动）\n");

        return new WorktreeDiffScope(label, changed, untracked.Count, body.ToString());
    }

    private static async Task<int> CountChangedFilesAsync(
        string repository, CancellationToken cancellation)
    {
        var names = await GitRunner.RunAsync(
            repository, ["diff", "--name-only", "HEAD"], cancellation);
        return names.Success
            ? names.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Count(line => line.Trim().Length > 0)
            : 0;
    }

    private static string Truncate(string body)
        => body.Length <= DiffBodyLimit
            ? body
            : body[..DiffBodyLimit]
              + $"\n\n…… 差异正文超过 {DiffBodyLimit} 字符，此处截断。"
              + "完整差异请在项目目录执行 git diff HEAD 查看。";
}
