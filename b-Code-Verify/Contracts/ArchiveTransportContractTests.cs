using System.IO;
using HistoryJanus.Git;
using Xunit;

namespace HistoryJanus.Contracts;

/// <summary>
/// 拉取走哪条路。传输一律是 SSH。归档记录里的 https 地址先换算，
/// 换不成就失败，不打开 HTTPS。一条 HTTPS 失败会拖住整轮刷新。
/// </summary>
public sealed class ArchiveTransportContractTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "HistoryJanus-Transport", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// SSH 地址换算成同一个仓库的 HTTPS 地址。最要紧的是后面几条不换：
    /// Windows 本地路径长得就是 scp 形式，被当成远端换成 https，本地裸仓的拉取会凭空失败。
    /// </summary>
    [Theory]
    [InlineData("git@github.com:cyx3705/2025-001.git", "https://github.com/cyx3705/2025-001.git")]
    [InlineData("ssh://git@github.com/cyx3705/2025-001.git", "https://github.com/cyx3705/2025-001.git")]
    [InlineData("ssh://git@ssh.github.com:443/cyx3705/2025-001.git", "https://github.com/cyx3705/2025-001.git")]
    [InlineData("git@gitee.com:owner/repo.git", "https://gitee.com/owner/repo.git")]
    [InlineData("https://github.com/cyx3705/2025-001.git", null)]
    [InlineData(@"C:\OneHistory\HistoryClio\remote.git", null)]
    [InlineData("/c/OneHistory/HistoryClio/remote.git", null)]
    [InlineData("", null)]
    public void SshRemotesMapToTheSameRepositoryOverHttps(string remote, string? expected)
    {
        var built = ArchiveCloner.TryBuildHttpsRemote(remote, out var https);
        Assert.Equal(expected != null, built);
        Assert.Equal(expected ?? string.Empty, https);
    }

    /// <summary>
    /// https 地址换成同一个仓库的 SSH。042 的 origin 就是这种没有 .git、带斜杠的写法。
    /// 已经是 SSH 或本地路径时不换，调用方继续用远程名 origin。
    /// </summary>
    [Theory]
    [InlineData("https://github.com/cyx3705/2026-042", "git@github.com:cyx3705/2026-042.git")]
    [InlineData("https://github.com/cyx3705/2026-042/", "git@github.com:cyx3705/2026-042.git")]
    [InlineData("https://github.com/cyx3705/2025-001.git", "git@github.com:cyx3705/2025-001.git")]
    [InlineData("http://gitee.com/owner/repo.git", "git@gitee.com:owner/repo.git")]
    [InlineData("https://ssh.github.com/cyx3705/2025-001.git", "git@github.com:cyx3705/2025-001.git")]
    [InlineData("git@github.com:cyx3705/2025-001.git", null)]
    [InlineData(@"C:\OneHistory\HistoryClio\remote.git", null)]
    [InlineData("", null)]
    public void HttpRemotesAreRewrittenToSsh(string remote, string? expected)
    {
        var built = ArchiveCloner.TryRewriteHttpToSsh(remote, out var ssh);
        Assert.Equal(expected != null, built);
        Assert.Equal(expected ?? string.Empty, ssh);
    }

    /// <summary>
    /// SSH 连不上时不再改走 HTTPS。死端口上失败一次就结束，进度里没有 HTTPS。
    /// </summary>
    [Fact]
    public async Task UnreachableSshDoesNotOpenHttps()
    {
        Directory.CreateDirectory(_root);
        var progress = new RecordingProgress();
        var result = await ArchiveCloner.CloneAsync(
            _root, "ssh://git@127.0.0.1:1/owner/repo.git", "main",
            Path.Combine(_root, "clone"), progress, CancellationToken.None);

        Assert.False(result.Success);
        Assert.DoesNotContain("HTTPS", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(progress.Lines, line => line.Contains("HTTPS", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>https 归档地址在克隆前换成 SSH，换不成的地址一次 git 都不跑。</summary>
    [Fact]
    public async Task UnrewritableHttpsFailsBeforeAnyClone()
    {
        Directory.CreateDirectory(_root);
        var progress = new RecordingProgress();
        var result = await ArchiveCloner.CloneAsync(
            _root, "https://intranet/owner/repo.git", "main",
            Path.Combine(_root, "clone"), progress, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("禁止使用 HTTPS", result.Message);
        Assert.Empty(progress.Lines);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) ProjectRepoLayout.DeleteTree(_root); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// 同步收进度。<see cref="Progress{T}"/> 把回调排到线程池，断言可能跑在最后一行之前。
    /// </summary>
    private sealed class RecordingProgress : IProgress<string>
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get { lock (_lines) return _lines.ToList(); }
        }

        public void Report(string value)
        {
            lock (_lines) _lines.Add(value);
        }
    }
}
