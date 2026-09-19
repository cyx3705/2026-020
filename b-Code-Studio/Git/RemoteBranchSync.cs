namespace HistoryJanus.Git;

/// <summary>
/// 远端有、本地还没落成分支的那些分支。三类分开记：能不能动、该不该动，理由不一样。
/// </summary>
/// <param name="Missing">远端有、本地连分支都没有——直接照远端建一条。</param>
/// <param name="Behind">本地有但落后于远端，且没被任何工作树签出——可以快进。</param>
/// <param name="Skipped">本地领先、已分叉，或正被某个工作树签出——一律不碰，只报原因。</param>
public sealed record RemoteBranchGap(
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Behind,
    IReadOnlyList<string> Skipped)
{
    public static readonly RemoteBranchGap Empty = new([], [], []);

    /// <summary>「同步」这一步真正会动的分支数。</summary>
    public int PendingCount => Missing.Count + Behind.Count;
}

/// <summary>「同步」时把远端分支落到本地的那一步结果。</summary>
public sealed record RemoteBranchSyncReport(
    int Created,
    int FastForwarded,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> Skipped);

/// <summary>
/// 远端分支的本地落地。
///
/// 刷新只取跟踪引用（<c>refs/remotes/origin/*</c>）：历史因此在本地、图谱也画得出来，
/// 但 <c>git branch</c> 里没有它们，检出、比较、开工作区都还得先自己敲一遍。
/// 「同步」这一步把它们补成真正的本地分支。
///
/// 三条硬规矩：
/// 1. **只快进，不合并、不改写**。本地领先或已分叉的分支一律跳过——那是人做过的事，
///    自动化不该替他决定怎么并。
/// 2. **被工作树签出的分支不碰**。AI 工作区是活的检出点，在它背后挪 HEAD 会让那个
///    工作区凭空变成「有一堆未提交改动」。
/// 3. **主线不走这里**。main 由 <c>SyncAsync</c> 自己的 <c>merge --ff-only</c> 处理，
///    两处都动同一条分支只会让「到底谁改的」说不清。
/// </summary>
internal static class RemoteBranchSync
{
    private const string RemotePrefix = "refs/remotes/origin/";
    private const string LocalPrefix = "refs/heads/";

    /// <summary>
    /// 读一次差距。只读本地引用，不联网——调用方负责在这之前 fetch。
    /// </summary>
    public static async Task<RemoteBranchGap> ReadGapAsync(
        string repository, CancellationToken cancellation)
    {
        var listed = await GitRunner.RunAsync(repository,
            ["for-each-ref", "--format=%(objectname)%09%(refname)", LocalPrefix, RemotePrefix],
            cancellation);
        if (!listed.Success)
            return RemoteBranchGap.Empty;

        var local = new Dictionary<string, string>(StringComparer.Ordinal);
        var remote = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in listed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length < 2)
                continue;
            var sha = parts[0].Trim();
            var reference = parts[1].Trim();
            if (reference.StartsWith(LocalPrefix, StringComparison.Ordinal))
                local[reference[LocalPrefix.Length..]] = sha;
            else if (reference.StartsWith(RemotePrefix, StringComparison.Ordinal))
                remote[reference[RemotePrefix.Length..]] = sha;
        }

        var candidates = remote.Keys.Where(IsSyncable).OrderBy(name => name, StringComparer.Ordinal).ToList();
        if (candidates.Count == 0)
            return RemoteBranchGap.Empty;

        var checkedOut = await ReadCheckedOutBranchesAsync(repository, cancellation);
        var missing = new List<string>();
        var behind = new List<string>();
        var skipped = new List<string>();
        foreach (var name in candidates)
        {
            if (!local.TryGetValue(name, out var localSha))
            {
                missing.Add(name);
                continue;
            }
            if (localSha.Equals(remote[name], StringComparison.OrdinalIgnoreCase))
                continue;
            if (checkedOut.Contains(name))
            {
                skipped.Add($"{name}（正被工作树签出）");
                continue;
            }
            var ancestor = await GitRunner.RunAsync(repository,
                ["merge-base", "--is-ancestor", localSha, remote[name]], cancellation);
            if (ancestor.ExitCode == 0)
                behind.Add(name);
            else
                skipped.Add($"{name}（本地领先或已分叉）");
        }

        return new RemoteBranchGap(missing, behind, skipped);
    }

    /// <summary>
    /// 把 <paramref name="gap"/> 里能动的那些动了。
    ///
    /// 新建用 <c>git branch</c>：它顺带把上游设成 <c>origin/&lt;名字&gt;</c>，
    /// 之后这条分支就和自己建的一样能 pull/push。快进用带旧值的 <c>update-ref</c>——
    /// 读到差距和真正写入之间若有别人动过这条分支，比较失败即放弃，不会覆盖掉。
    /// </summary>
    public static async Task<RemoteBranchSyncReport> MaterializeAsync(
        string repository,
        RemoteBranchGap gap,
        IProgress<string>? progress,
        CancellationToken cancellation)
    {
        var created = 0;
        var fastForwarded = 0;
        var failures = new List<string>();

        foreach (var name in gap.Missing)
        {
            var result = await GitRunner.RunAsync(repository,
                ["branch", "--track", name, RemotePrefix + name], cancellation);
            if (result.Success)
            {
                created++;
                progress?.Report($"新建本地分支 {name}");
            }
            else
            {
                failures.Add($"{name}: {FirstLine(result.Output)}");
            }
        }

        foreach (var name in gap.Behind)
        {
            var remoteSha = await ReadShaAsync(repository, RemotePrefix + name, cancellation);
            var localSha = await ReadShaAsync(repository, LocalPrefix + name, cancellation);
            if (remoteSha.Length == 0 || localSha.Length == 0)
            {
                failures.Add($"{name}: 读取引用失败");
                continue;
            }
            var result = await GitRunner.RunAsync(repository,
                ["update-ref", LocalPrefix + name, remoteSha, localSha], cancellation);
            if (result.Success)
            {
                fastForwarded++;
                progress?.Report($"快进本地分支 {name}");
            }
            else
            {
                failures.Add($"{name}: {FirstLine(result.Output)}");
            }
        }

        return new RemoteBranchSyncReport(created, fastForwarded, failures, gap.Skipped);
    }

    /// <summary>一句话说清这次分支侧做了什么；没动就返回空串，由调用方决定要不要接。</summary>
    public static string Describe(RemoteBranchSyncReport report)
    {
        var parts = new List<string>();
        if (report.Created > 0)
            parts.Add($"新建 {report.Created} 条");
        if (report.FastForwarded > 0)
            parts.Add($"快进 {report.FastForwarded} 条");
        if (report.Skipped.Count > 0)
            parts.Add($"跳过 {report.Skipped.Count} 条（{string.Join("、", report.Skipped)}）");
        if (report.Failures.Count > 0)
            parts.Add($"失败 {report.Failures.Count} 条（{string.Join("、", report.Failures)}）");
        return parts.Count == 0 ? "" : $"远端分支：{string.Join("，", parts)}";
    }

    /// <summary>
    /// 主线由 SyncAsync 自己处理；<c>origin/HEAD</c> 是指向别的分支的符号引用，
    /// 照着它建一条本地分支只会多出一条名叫 HEAD 的重影。
    /// </summary>
    private static bool IsSyncable(string name)
        => name.Length > 0
           && !name.Equals(ProjectService.MainlineBranch, StringComparison.OrdinalIgnoreCase)
           && !name.Equals("HEAD", StringComparison.Ordinal);

    /// <summary>当前仓的全部检出点（含 AI 工作区）各自签出了哪条分支。</summary>
    private static async Task<HashSet<string>> ReadCheckedOutBranchesAsync(
        string repository, CancellationToken cancellation)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var result = await GitRunner.RunAsync(repository,
            ["worktree", "list", "--porcelain"], cancellation);
        if (!result.Success)
            return set;
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var text = line.TrimEnd('\r');
            if (text.StartsWith("branch " + LocalPrefix, StringComparison.Ordinal))
                set.Add(text[("branch " + LocalPrefix).Length..].Trim());
        }
        return set;
    }

    private static async Task<string> ReadShaAsync(
        string repository, string reference, CancellationToken cancellation)
    {
        var result = await GitRunner.RunAsync(repository,
            ["rev-parse", "--verify", reference], cancellation);
        return result.Success ? FirstLine(result.Output) : "";
    }

    private static string FirstLine(string value)
        => value.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
}
