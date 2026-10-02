namespace HistoryJanus.Git;

/// <summary>交给 AI 判断的分叉事实。全部由 git 读出，AI 只看、不执行任何命令。</summary>
/// <param name="Strategy">Janus 已经定好的合并方式（rebase / merge），AI 不能改。</param>
/// <param name="OverlapDiff">两边都改过的文件各自相对分叉点的差异，已截断。</param>
public sealed record DivergenceFacts(
    string Branch,
    string MergeBase,
    IReadOnlyList<string> LocalCommits,
    IReadOnlyList<string> RemoteCommits,
    IReadOnlyList<string> LocalFiles,
    IReadOnlyList<string> RemoteFiles,
    IReadOnlyList<string> OverlapFiles,
    string OverlapDiff,
    string Strategy);

/// <summary>AI 的判定。</summary>
/// <param name="Proceed">true = 简单情况，可以自动合并。</param>
/// <param name="Failure">调用本身失败（Apollo 没装、没密钥、断网、答非 JSON）时的原因；有值时一律不执行。</param>
public sealed record DivergenceAdvice(bool Proceed, string Reason, string? Failure = null);

/// <summary>分叉检查通道。为空时分叉照旧不自动处理。</summary>
public interface IDivergenceAdvisor
{
    Task<DivergenceAdvice> AdviseAsync(DivergenceFacts facts, CancellationToken cancellation);
}

public enum ReconcileOutcome
{
    /// <summary>本地不落后于远端，无事可做。</summary>
    NotDiverged,
    /// <summary>已快进或已合并，本地现在包含远端。</summary>
    Reconciled,
    /// <summary>真冲突（git 文本冲突或 AI 判定有风险）：什么都没执行。</summary>
    Conflict,
    /// <summary>前提不满足（工作树不干净、没接 AI、AI 不可用）：什么都没执行。</summary>
    Refused,
    /// <summary>执行中失败，已回滚到执行前。</summary>
    Failed,
}

public sealed record ReconcileResult(ReconcileOutcome Outcome, string Message, string Strategy = "")
{
    public bool Success => Outcome is ReconcileOutcome.NotDiverged or ReconcileOutcome.Reconciled;

    /// <summary>真的动过本地分支（快进或合并），调用方据此决定要不要继续推送。</summary>
    public bool Changed => Outcome == ReconcileOutcome.Reconciled;
}

/// <summary>
/// 5.15.0：本地与远端分叉时的自动收口（推送被拒、同步遇到分叉都走这里）。
///
/// 分工是固定的：**git 判文本冲突，AI 判语义风险，Janus 执行**。
/// 1. 工作树必须干净，否则不动。
/// 2. 只落后不领先就是快进，不问 AI。
/// 3. 真分叉先用 <c>git merge-tree --write-tree</c> 在内存里试合，有文本冲突直接返回冲突文件，不问 AI、不碰工作树。
/// 4. 文本无冲突时把两边提交、改动文件和交叠文件的差异交给 AI，AI 只回答「能不能自动合」。
///    AI 不可用或答不上来一律不执行——宁可让人处理，也不在没检查的情况下改历史。
/// 5. 合并方式由 Janus 定：本地提交全是未发布的线性提交时变基（历史保持一条线），
///    否则（含合并提交、或已出现在任一远端分支上）用 merge，避免改写别人已经拿到的提交。
/// 6. 执行失败立即 abort，并核对 HEAD 回到执行前。
/// </summary>
public static class DivergenceReconciler
{
    public const string RebaseStrategy = "rebase";
    public const string MergeStrategy = "merge";

    private const int MaxListed = 40;
    private const int MaxDiffChars = 8000;

    public static async Task<ReconcileResult> ReconcileAsync(
        string repo,
        string branch,
        IDivergenceAdvisor? advisor,
        IProgress<string>? progress,
        CancellationToken cancellation)
    {
        var upstream = $"refs/remotes/origin/{branch}";
        var counts = await GitRunner.RunAsync(repo,
            ["rev-list", "--left-right", "--count", $"HEAD...{upstream}"], cancellation);
        if (!counts.Success)
            return new(ReconcileOutcome.Failed, $"无法比较本地与 origin/{branch}:\n{counts.Output}");
        var parts = counts.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var ahead = parts.Length > 0 && int.TryParse(parts[0], out var a) ? a : 0;
        var behind = parts.Length > 1 && int.TryParse(parts[1], out var b) ? b : 0;
        if (behind == 0)
            return new(ReconcileOutcome.NotDiverged, "本地不落后于远端");

        var status = await GitRunner.RunAsync(repo,
            ["status", "--porcelain=v1", "--untracked-files=all"], cancellation);
        if (!status.Success || !string.IsNullOrWhiteSpace(status.Output))
            return new(ReconcileOutcome.Refused, "工作树有未提交或未跟踪内容，不自动合并；请先提交");

        var before = await HeadAsync(repo, cancellation);
        if (ahead == 0)
        {
            var forward = await GitRunner.RunAsync(repo, ["merge", "--ff-only", upstream], cancellation);
            return forward.Success
                ? new(ReconcileOutcome.Reconciled, $"已快进到 origin/{branch}（远端领先 {behind} 个提交）", "ff")
                : new(ReconcileOutcome.Failed, $"快进失败:\n{forward.Output}");
        }

        if (advisor == null)
            return new(ReconcileOutcome.Refused,
                $"本地与远端已分叉（本地 {ahead} / 远端 {behind}），未接入 AI 检查，不自动合并");

        progress?.Report($"[{branch}] 已分叉：本地 {ahead} / 远端 {behind}，试合检查文本冲突...");
        var probe = await GitRunner.RunAsync(repo,
            ["merge-tree", "--write-tree", "--name-only", "--no-messages", "HEAD", upstream], cancellation);
        if (probe.ExitCode == 1)
        {
            var conflicted = Lines(probe.Output).Skip(1).Distinct(StringComparer.Ordinal).ToList();
            return new(ReconcileOutcome.Conflict,
                $"本地与远端已分叉（本地 {ahead} / 远端 {behind}），存在真冲突，未执行合并。冲突文件:\n  - "
                + string.Join("\n  - ", conflicted.Take(MaxListed)));
        }
        if (!probe.Success)
            return new(ReconcileOutcome.Failed, $"试合失败（需要 git 2.38+）:\n{probe.Output}");

        var facts = await ReadFactsAsync(repo, branch, upstream, ahead, cancellation);
        progress?.Report($"[{branch}] 文本无冲突；交给 AI 检查（{facts.Strategy}，交叠文件 {facts.OverlapFiles.Count} 个）...");
        DivergenceAdvice advice;
        try
        {
            advice = await advisor.AdviseAsync(facts, cancellation);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            advice = new DivergenceAdvice(false, string.Empty, ex.Message);
        }
        if (advice.Failure != null)
            return new(ReconcileOutcome.Refused,
                $"本地与远端已分叉（本地 {ahead} / 远端 {behind}），AI 检查不可用，未执行合并：{advice.Failure}");
        if (!advice.Proceed)
            return new(ReconcileOutcome.Conflict,
                $"本地与远端已分叉（本地 {ahead} / 远端 {behind}），AI 判定不宜自动合并，未执行：{advice.Reason}");

        progress?.Report($"[{branch}] AI 判定可自动合并（{advice.Reason}），执行 {facts.Strategy}...");
        return await ExecuteAsync(repo, branch, upstream, facts.Strategy, before, ahead, behind,
            advice.Reason, cancellation);
    }

    private static async Task<ReconcileResult> ExecuteAsync(
        string repo, string branch, string upstream, string strategy, string before,
        int ahead, int behind, string reason, CancellationToken cancellation)
    {
        GitResult run;
        if (strategy == RebaseStrategy)
        {
            run = await GitRunner.RunAsync(repo, ["rebase", upstream], cancellation);
            if (!run.Success)
                await GitRunner.RunAsync(repo, ["rebase", "--abort"], CancellationToken.None);
        }
        else
        {
            run = await GitRunner.RunAsync(repo,
                ["merge", "--no-ff", "--no-edit", "-m", $"合并 origin/{branch}（Janus 分叉自动合并）", upstream],
                cancellation);
            if (!run.Success)
                await GitRunner.RunAsync(repo, ["merge", "--abort"], CancellationToken.None);
        }

        if (!run.Success)
        {
            var now = await HeadAsync(repo, CancellationToken.None);
            var restored = now.Equals(before, StringComparison.OrdinalIgnoreCase)
                ? "已回滚到执行前"
                : $"回滚后 HEAD 为 {Short(now)}，与执行前 {Short(before)} 不一致，请人工检查";
            return new(ReconcileOutcome.Failed, $"自动 {strategy} 失败，{restored}:\n{run.Output}", strategy);
        }

        var contains = await GitRunner.RunAsync(repo,
            ["merge-base", "--is-ancestor", upstream, "HEAD"], cancellation);
        if (!contains.Success)
            return new(ReconcileOutcome.Failed, $"自动 {strategy} 后本地仍不包含 origin/{branch}，请人工检查", strategy);

        var how = strategy == RebaseStrategy
            ? $"本地 {ahead} 个提交已变基到远端 {behind} 个提交之后"
            : $"已生成合并提交，合入远端 {behind} 个提交";
        return new(ReconcileOutcome.Reconciled, $"分叉已自动收口：{how}。AI 检查：{reason}", strategy);
    }

    private static async Task<DivergenceFacts> ReadFactsAsync(
        string repo, string branch, string upstream, int ahead, CancellationToken cancellation)
    {
        var mergeBase = FirstLine((await GitRunner.RunAsync(repo,
            ["merge-base", "HEAD", upstream], cancellation)).Output);
        var localCommits = await ListAsync(repo,
            ["log", "--format=%h %s", $"-{MaxListed}", $"{mergeBase}..HEAD"], cancellation);
        var remoteCommits = await ListAsync(repo,
            ["log", "--format=%h %an: %s", $"-{MaxListed}", $"{mergeBase}..{upstream}"], cancellation);
        var localFiles = await ListAsync(repo, ["diff", "--name-only", mergeBase, "HEAD"], cancellation);
        var remoteFiles = await ListAsync(repo, ["diff", "--name-only", mergeBase, upstream], cancellation);
        var overlap = localFiles.Intersect(remoteFiles, StringComparer.Ordinal).ToList();

        var diff = string.Empty;
        if (overlap.Count > 0)
        {
            var paths = overlap.Take(MaxListed).Select(path => $":(literal){path}").ToList();
            var local = await GitRunner.RunAsync(repo,
                ["diff", "--unified=2", mergeBase, "HEAD", "--", .. paths], cancellation);
            var remote = await GitRunner.RunAsync(repo,
                ["diff", "--unified=2", mergeBase, upstream, "--", .. paths], cancellation);
            diff = Clip("【本地相对分叉点】\n" + local.Output, MaxDiffChars / 2) + "\n"
                   + Clip("【远端相对分叉点】\n" + remote.Output, MaxDiffChars / 2);
        }

        // 变基会改写本地提交：只在本地提交全是线性、且没有任何一条已出现在远端分支上时才用。
        var merges = await GitRunner.RunAsync(repo,
            ["rev-list", "--count", "--merges", $"{mergeBase}..HEAD"], cancellation);
        var unpublished = await GitRunner.RunAsync(repo,
            ["rev-list", "--count", "HEAD", "--not", "--remotes"], cancellation);
        var linear = merges.Success && FirstLine(merges.Output) == "0";
        var unpublishedOnly = unpublished.Success && FirstLine(unpublished.Output) == ahead.ToString();
        var strategy = linear && unpublishedOnly ? RebaseStrategy : MergeStrategy;

        return new DivergenceFacts(branch, mergeBase, localCommits, remoteCommits,
            localFiles, remoteFiles, overlap, diff, strategy);
    }

    private static async Task<List<string>> ListAsync(
        string repo, IReadOnlyList<string> arguments, CancellationToken cancellation)
    {
        var result = await GitRunner.RunAsync(repo, arguments, cancellation);
        return result.Success ? Lines(result.Output).ToList() : [];
    }

    private static async Task<string> HeadAsync(string repo, CancellationToken cancellation)
        => FirstLine((await GitRunner.RunAsync(repo, ["rev-parse", "HEAD"], cancellation)).Output);

    private static IEnumerable<string> Lines(string text)
        => text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static string FirstLine(string text) => Lines(text).FirstOrDefault() ?? string.Empty;

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    private static string Clip(string text, int max)
        => text.Length <= max ? text : text[..max] + "\n…（已截断）";
}
