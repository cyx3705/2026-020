namespace HistoryJanus.Git;

/// <summary>
/// 项目级推送计划（5.16.4，用户定：同步与归档作用在项目上，不看当前停在哪条分支）。
///
/// 一个项目「推完了」= main 不领先 origin/main，且每条**有自己内容**的本地分支都已在 origin 上、不领先。
/// 「有自己内容」= 没并入 main：并进 main 的分支内容随 main 一起上去了，归档也丢不了什么。
/// 开发管线的 <c>ai/…</c> 分支是管线自己的检出点，不归人推，一律不算。
/// </summary>
internal static class BranchPushPlan
{
    /// <summary>开发管线（vulcan.dev.*）建的分支前缀。</summary>
    internal const string PipelinePrefix = "ai/";

    private const string LocalPrefix = "refs/heads/";
    private const string RemotePrefix = "refs/remotes/origin/";

    /// <summary>除 main 之外、需要推上去的本地分支：远端没有，或本地领先远端（未分叉）。只读本地引用。</summary>
    public static async Task<IReadOnlyList<string>> UnpushedSideBranchesAsync(
        string repository, CancellationToken cancellation)
    {
        var listed = await GitRunner.RunAsync(repository,
            ["for-each-ref", "--format=%(objectname)%09%(refname)", LocalPrefix, RemotePrefix], cancellation);
        if (!listed.Success)
            return [];
        var local = new Dictionary<string, string>(StringComparer.Ordinal);
        var remote = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in listed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length < 2)
                continue;
            if (parts[1].StartsWith(LocalPrefix, StringComparison.Ordinal))
                local[parts[1][LocalPrefix.Length..]] = parts[0].Trim();
            else if (parts[1].StartsWith(RemotePrefix, StringComparison.Ordinal))
                remote[parts[1][RemotePrefix.Length..]] = parts[0].Trim();
        }

        var merged = await GitRunner.RunAsync(repository,
            ["branch", "--merged", LocalPrefix + ProjectService.MainlineBranch, "--format=%(refname:short)"], cancellation);
        var mergedNames = merged.Success
            ? merged.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal)
            : [];

        var pending = new List<string>();
        foreach (var (name, sha) in local.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (name == ProjectService.MainlineBranch
                || name.StartsWith(PipelinePrefix, StringComparison.Ordinal)
                || mergedNames.Contains(name))
                continue;
            if (!remote.TryGetValue(name, out var remoteSha))
            {
                pending.Add(name);
                continue;
            }
            if (sha.Equals(remoteSha, StringComparison.OrdinalIgnoreCase))
                continue;
            // 远端是本地的祖先 = 本地领先，推得上去；分叉或落后的交给「同步」，这里不算。
            var ancestor = await GitRunner.RunAsync(repository,
                ["merge-base", "--is-ancestor", remoteSha, sha], cancellation);
            if (ancestor.ExitCode == 0)
                pending.Add(name);
        }
        return pending;
    }

    /// <summary>左右各领先几个提交；读不出来返回 null。</summary>
    public static async Task<(int Left, int Right)?> CountAsync(
        string repository, string left, string right, CancellationToken cancellation)
    {
        var counts = await GitRunner.RunAsync(repository,
            ["rev-list", "--left-right", "--count", $"{left}...{right}"], cancellation);
        if (!counts.Success)
            return null;
        var parts = counts.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var a = parts.Length > 0 && int.TryParse(parts[0], out var l) ? l : 0;
        var b = parts.Length > 1 && int.TryParse(parts[1], out var r) ? r : 0;
        return (a, b);
    }

    /// <summary>一句话列出要推的分支，太多时只写前三条。</summary>
    public static string Describe(IReadOnlyList<string> branches)
        => branches.Count <= 3
            ? string.Join("、", branches)
            : $"{string.Join("、", branches.Take(3))} 等 {branches.Count} 条";
}
