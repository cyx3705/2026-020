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
        if (!cherry.Success || cherry.Output.Split('\n').Any(line => line.StartsWith("+ ", StringComparison.Ordinal)))
            return true;

        // cherry 不检查 merge commit 的冲突解决，独有 merge 必须保守保留。
        var merges = await GitRunner.RunAsync(repo,
            ["rev-list", "--merges", "--max-count=1", $"{mainline}..{branch}"], cancellation);
        return !merges.Success || !string.IsNullOrWhiteSpace(merges.Output);
    }
}
