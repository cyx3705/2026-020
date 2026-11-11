namespace HistoryJanus.Git;

/// <summary>REQ-BRANCH-023：分支是否仍有未进入主线的独立改动；只读，不删除引用。</summary>
internal static class BranchIndependence
{
    internal static async Task<bool> HasIndependentChangesAsync(
        string repo, string mainline, string branch, CancellationToken cancellation)
    {
        // cherry 识别变基 / cherry-pick 后 SHA 不同、补丁相同的提交。
        // Git 失败时保留分支，不能把无法判定当作已合并。
        var cherry = await GitRunner.RunAsync(repo, ["cherry", mainline, branch], cancellation);
        if (!cherry.Success)
            return true;

        foreach (var line in cherry.Output.Split('\n').Where(line => line.StartsWith("+ ", StringComparison.Ordinal)))
        {
            // cherry 只比较分叉点以后的主线补丁。补接旧历史后，等价补丁可能已在共同祖先之前。
            // 用同题提交作有限候选检索，再逐个验证补丁；题目本身不构成已合并证据。
            if (!await HasEarlierEquivalentAsync(repo, mainline, line[2..].Trim(), cancellation))
                return true;
        }

        // cherry 不检查 merge commit 的冲突解决，独有 merge 必须保守保留。
        var merges = await GitRunner.RunAsync(repo,
            ["rev-list", "--merges", $"{mainline}..{branch}"], cancellation);
        if (!merges.Success)
            return true;
        foreach (var merge in merges.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // 与第一父提交的树完全相同，说明只是补接历史，没有独有的冲突解决内容。
            var diff = await GitRunner.RunAsync(repo,
                ["diff", "--quiet", "--no-ext-diff", $"{merge}^1", merge, "--"], cancellation);
            if (!diff.Success)
                return true;
        }
        return false;
    }

    private static async Task<bool> HasEarlierEquivalentAsync(
        string repo, string mainline, string commit, CancellationToken cancellation)
    {
        var subject = await GitRunner.RunAsync(repo, ["show", "-s", "--format=%s", commit], cancellation);
        if (!subject.Success || string.IsNullOrWhiteSpace(subject.Output))
            return false;
        var candidates = await GitRunner.RunAsync(repo,
            ["log", "--no-merges", "--format=%H", "--fixed-strings", $"--grep={subject.Output.Trim()}", mainline, "--"],
            cancellation);
        if (!candidates.Success || string.IsNullOrWhiteSpace(candidates.Output))
            return false;
        var expected = await PatchIdAsync(repo, commit, cancellation);
        if (expected == null)
            return false;
        foreach (var candidate in candidates.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (expected == await PatchIdAsync(repo, candidate, cancellation))
                return true;
        return false;
    }

    private static async Task<string?> PatchIdAsync(string repo, string commit, CancellationToken cancellation)
    {
        var patch = await GitRunner.RunAsync(repo,
            ["show", "--format=", "--full-index", "--no-ext-diff", "--no-textconv", "--no-renames", commit, "--"], cancellation);
        if (!patch.Success || string.IsNullOrWhiteSpace(patch.Output))
            return null;
        var id = await GitRunner.RunWithInputAsync(repo, ["patch-id", "--stable"], patch.Output, cancellation);
        return id.Success ? id.Output.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() : null;
    }
}
