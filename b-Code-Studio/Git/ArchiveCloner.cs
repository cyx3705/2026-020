using System.IO;
using System.Text;

namespace HistoryJanus.Git;

/// <summary>
/// 把归档项目从远端取回来的传输策略。
///
/// 为什么不是一条 <c>git clone</c>：本机链路上超过约 300MB 的单次传输会断，这是
/// 2026-08 全库首推时实测出来的事实，推送侧据此有了 <see cref="ProjectService.PushChunkBudgetBytes"/>
/// 分批（见 <c>ProjectService.Push.cs</c>）。拉取一直是整包克隆，于是在大仓上稳定地撞同一堵墙：
/// <c>fetch-pack: unexpected disconnect while reading sideband packet / early EOF /
/// invalid index-pack output</c>，而且失败之前没有任何进度，界面看上去就是卡死的。
///
/// 这里把一次整包克隆拆成「浅克隆 + 反复加深」：每一段都是一个小得多的 pack，
/// 断了只重传那一段，而不是从头再来。三件事同时成立才算修好：
/// <list type="number">
///   <item>每一步都有进度（<c>--progress</c> 加 <see cref="GitRunner.RunStreamingAsync"/>），
///         人能看出它在动还是真卡住；</item>
///   <item>传输失败可重试，不是一次定生死；</item>
///   <item>LFS 内容单独一步取，它的失败不连累已经落地的 git 历史。</item>
/// </list>
///
/// 分段解决的是「传着传着断了」。传输一律走 SSH。归档记录里若是 https 地址，
/// 克隆前先换成 <c>git@host:path.git</c>，换不成就直接失败，不打开 HTTPS。
/// 一条 HTTPS 连接重置会让整轮刷新一直等。
/// </summary>
internal static class ArchiveCloner
{
    /// <summary>每次加深取多少个提交。小到一段传输能过去，大到不至于跑几百轮。</summary>
    private const int DeepenStep = 250;

    /// <summary>加深的轮数上限。到顶只说明这仓比预期深得多，按失败报出，不无限转。</summary>
    private const int MaxDeepenRounds = 400;

    /// <summary>同一步传输最多试几次。</summary>
    private const int MaxAttempts = 3;

    /// <summary>
    /// 可重试的传输失败。这些都是链路中断留下的痕迹，不是服务端拒收——
    /// 服务端拒收（权限、分支不存在、pre-receive）重试多少次都是同一个结果，
    /// 把它们也重试只会让人多等两轮才看到真正的原因。
    /// </summary>
    private static readonly string[] RetryableMarkers =
    [
        "early EOF",
        "unexpected disconnect",
        "index-pack",
        "RPC failed",
        "Connection reset",
        "Connection timed out",
        "remote end hung up",
        "unable to access",
    ];

    /// <summary>GitHub 的 SSH 备用口专用域名，换算时还原成主域名。</summary>
    private const string GitHubSshAlias = "ssh.github.com";

    /// <summary>克隆期间不铺开 LFS 内容：先把 git 历史落地，实体稍后单独取。</summary>
    private static readonly Dictionary<string, string> SkipLfsSmudge =
        new(StringComparer.Ordinal) { ["GIT_LFS_SKIP_SMUDGE"] = "1" };

    /// <summary>
    /// 取回一个归档项目。成功时 <c>Message</c> 为空串，或是一句「LFS 没取全」的补充说明——
    /// 调用方把它接在成功消息后面，不要当失败处理。
    /// </summary>
    public static async Task<(bool Success, string Message)> CloneAsync(
        string libraryRoot,
        string remote,
        string branch,
        string destination,
        IProgress<string>? progress,
        CancellationToken cancellation)
    {
        var clone = await ShallowCloneAsync(
            libraryRoot, remote, branch, destination, progress, cancellation);
        if (!clone.Success)
            return (false, clone.Message);

        var deepened = await DeepenAsync(destination, branch, progress, cancellation);
        if (!deepened.Success)
            return (false, deepened.Message);

        var lfs = await FetchLfsAsync(destination, progress, cancellation);
        return (true, clone.Message + lfs.Message);
    }

    /// <summary>
    /// 第一段浅克隆。https 地址先换成 SSH 再克隆；换不成则一次网络都不发。
    /// </summary>
    private static async Task<(bool Success, string Message)> ShallowCloneAsync(
        string libraryRoot,
        string remote,
        string branch,
        string destination,
        IProgress<string>? progress,
        CancellationToken cancellation)
    {
        if (!TryPrepareTransport(remote, out remote, out var denied))
            return (false, denied);
        progress?.Report($"浅克隆 {branch}（深度 {DeepenStep}）...");
        var direct = await TransferAsync(
            libraryRoot, CloneArguments(remote, branch, destination), "浅克隆", progress, cancellation);
        return direct.Success ? (true, string.Empty) : (false, direct.Message);
    }

    private static string[] CloneArguments(string remote, string branch, string destination) =>
    [
        "-c", "lfs.locksverify=false", "clone", "--progress", "--depth", DeepenStep.ToString(),
        "--branch", branch, "--single-branch", remote, destination,
    ];

    /// <summary>
    /// 把 SSH 形式的 git 地址换算成同一个仓库的 HTTPS 地址：
    /// <c>git@host:owner/repo.git</c> 与 <c>ssh://git@host:port/owner/repo.git</c>
    /// 都得到 <c>https://host/owner/repo.git</c>。已经是 HTTPS 的原样拒绝——没有可换的路。
    ///
    /// 主机名必须带点：Windows 本地路径（<c>C:\OneHistory\...</c>）长得就是 scp 形式，
    /// 少这一条，一个本地裸仓会被当成主机名为 <c>C</c> 的远端换成 https。
    /// </summary>
    internal static bool TryBuildHttpsRemote(string remote, out string https)
    {
        https = string.Empty;
        var value = remote.Trim();
        if (value.Length == 0 || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return false;

        string authority;
        string path;
        if (value.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            var rest = value["ssh://".Length..];
            var slash = rest.IndexOf('/');
            if (slash <= 0)
                return false;
            authority = rest[..slash];
            path = rest[(slash + 1)..];
        }
        else
        {
            var colon = value.IndexOf(':');
            var firstSlash = value.IndexOf('/');
            if (colon <= 0 || (firstSlash >= 0 && firstSlash < colon))
                return false;
            authority = value[..colon];
            path = value[(colon + 1)..];
        }

        var user = authority.LastIndexOf('@');
        if (user >= 0)
            authority = authority[(user + 1)..];
        var port = authority.IndexOf(':');
        if (port >= 0)
            authority = authority[..port];
        if (authority.Equals(GitHubSshAlias, StringComparison.OrdinalIgnoreCase))
            authority = "github.com";

        path = path.TrimStart('/');
        if (!authority.Contains('.') || authority.Contains('\\') || path.Length == 0 || path.Contains('\\'))
            return false;

        https = $"https://{authority}/{path}";
        return true;
    }

    /// <summary>
    /// 传输地址。已经是 SSH、本地路径或其他非 http 形式时原样返回。
    /// <c>https://host/owner/repo</c> 换成 <c>git@host:owner/repo.git</c>。
    /// 主机名必须带点，避免把 Windows 路径当成网址。换不成时 <paramref name="transport"/> 为空。
    /// </summary>
    internal static bool TryPrepareTransport(string remote, out string transport, out string error)
    {
        transport = remote.Trim();
        error = string.Empty;
        if (transport.Length == 0)
        {
            error = "没有远端地址";
            return false;
        }
        if (!IsHttpUrl(transport))
            return true;
        if (TryRewriteHttpToSsh(transport, out var ssh))
        {
            transport = ssh;
            return true;
        }
        error = $"禁止使用 HTTPS 传输，且这个地址换不成 SSH: {transport}";
        transport = string.Empty;
        return false;
    }

    /// <summary>
    /// <c>https://host/owner/repo</c> 与带 <c>.git</c>、末尾斜杠的同址，都得到
    /// <c>git@host:owner/repo.git</c>。已经是 SSH 或本地路径时返回 false，调用方继续用原地址。
    /// </summary>
    internal static bool TryRewriteHttpToSsh(string remote, out string ssh)
    {
        ssh = string.Empty;
        var value = remote.Trim();
        if (!IsHttpUrl(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return false;
        if (!uri.Host.Contains('.') || uri.Host.Contains('\\'))
            return false;
        var path = uri.AbsolutePath.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];
        if (path.Length == 0 || path.Contains('\\') || path.Contains(' '))
            return false;
        var host = uri.Host;
        if (host.Equals(GitHubSshAlias, StringComparison.OrdinalIgnoreCase))
            host = "github.com";
        ssh = $"git@{host}:{path}.git";
        return true;
    }

    internal static bool IsHttpUrl(string remote)
    {
        var value = remote.Trim();
        return value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 反复加深到不再有更早的提交，最后摘掉浅标记。
    /// 判「到底了」用提交数不再增长，而不是 git 有没有自己清掉 <c>.git/shallow</c>——
    /// 后者在不同 git 版本上不一致，据此退出会在某些仓上多转一轮、在另一些仓上早退。
    /// </summary>
    private static async Task<(bool Success, string Message)> DeepenAsync(
        string repository,
        string branch,
        IProgress<string>? progress,
        CancellationToken cancellation)
    {
        var previous = await CountCommitsAsync(repository, cancellation);
        for (var round = 1; round <= MaxDeepenRounds; round++)
        {
            if (!await IsShallowAsync(repository, cancellation))
                return (true, string.Empty);

            progress?.Report($"加深第 {round} 段（已有 {previous} 个提交）...");
            var fetch = await TransferAsync(
                repository,
                ["fetch", "--progress", "--no-tags", $"--deepen={DeepenStep}", "origin", branch],
                $"加深第 {round} 段", progress, cancellation);
            if (!fetch.Success)
                return (false, fetch.Message);

            var current = await CountCommitsAsync(repository, cancellation);
            if (current > previous)
            {
                previous = current;
                continue;
            }

            // 没有更早的提交了，剩下的只是摘掉浅标记，这一步不传输内容。
            progress?.Report("已到根提交，摘除浅克隆标记...");
            var unshallow = await TransferAsync(
                repository,
                ["fetch", "--progress", "--no-tags", "--unshallow", "origin", branch],
                "摘除浅标记", progress, cancellation);
            return unshallow.Success ? (true, string.Empty) : (false, unshallow.Message);
        }

        return (false, $"加深超过 {MaxDeepenRounds} 段仍未取完历史，已中止");
    }

    /// <summary>
    /// 取回 LFS 实体。失败**不**算拉取失败：git 历史与工作树此刻已经完整，
    /// 缺的只是几个大文件的内容，为一次重试回滚整个项目不划算。
    /// 这里的话由调用方原样带进成功消息，人自己决定要不要重跑。
    /// </summary>
    private static async Task<(bool Success, string Message)> FetchLfsAsync(
        string repository,
        IProgress<string>? progress,
        CancellationToken cancellation)
    {
        var tracked = await GitRunner.RunAsync(repository, ["lfs", "ls-files", "-n"], cancellation);
        if (!tracked.Success || tracked.Output.Trim().Length == 0)
            return (true, string.Empty);

        progress?.Report("取回 Git LFS 内容...");
        var pull = await TransferAsync(repository, ["lfs", "pull"], "LFS 取回", progress, cancellation);
        return pull.Success
            ? (true, string.Empty)
            : (false, $"；Git LFS 内容未取全，可在项目目录重跑 git lfs pull：{FirstLine(pull.Message)}");
    }

    /// <summary>
    /// 一步传输，带重试。重试之间不等待：链路是瞬时断的，等待只让人多看几秒。
    /// </summary>
    private static async Task<(bool Success, string Message)> TransferAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        string step,
        IProgress<string>? progress,
        CancellationToken cancellation)
    {
        var result = new GitResult(-1, string.Empty);
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            result = await GitRunner.RunStreamingAsync(
                workingDirectory, arguments, progress, cancellation, SkipLfsSmudge);
            if (result.Success)
                return (true, string.Empty);
            if (!IsRetryable(result.Output) || attempt == MaxAttempts)
                break;
            progress?.Report($"{step}传输中断，重试 {attempt + 1}/{MaxAttempts}：{FirstLine(result.Output)}");
        }

        var detail = new StringBuilder(step).Append("失败");
        if (IsRetryable(result.Output))
            detail.Append($"（已重试 {MaxAttempts} 次，均为链路中断）");
        detail.Append(":\n").Append(result.Output);
        return (false, detail.ToString());
    }

    private static bool IsRetryable(string output)
        => RetryableMarkers.Any(marker => output.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static async Task<bool> IsShallowAsync(string repository, CancellationToken cancellation)
    {
        var shallow = await GitRunner.RunAsync(
            repository, ["rev-parse", "--is-shallow-repository"], cancellation);
        return shallow.Success
               && FirstLine(shallow.Output).Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<int> CountCommitsAsync(string repository, CancellationToken cancellation)
    {
        var count = await GitRunner.RunAsync(repository, ["rev-list", "--count", "HEAD"], cancellation);
        return count.Success && int.TryParse(FirstLine(count.Output), out var value) ? value : 0;
    }

    private static string FirstLine(string value)
        => value.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim() ?? string.Empty;
}
