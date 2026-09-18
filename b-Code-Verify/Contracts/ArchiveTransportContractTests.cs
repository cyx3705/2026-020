using System.IO;
using HistoryJanus.Git;
using Xunit;

namespace HistoryJanus.Contracts;

/// <summary>
/// 拉取走哪条路。归档记录里存的是仓库当初的 origin，通常是 SSH；
/// 本机到 GitHub 的 SSH 口被重置时，取回必须自己换一条走得通的路，
/// 而不是重试三次以后把「Connection reset」原样报给人看。
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
    /// SSH 连不上时不能就此收手：要换 HTTPS 再试一次，换路这件事进度里说得出口，
    /// 两条路都不通时失败消息里两条都有交代。两端都指向本机死端口，不碰外网。
    /// </summary>
    [Fact]
    public async Task UnreachableSshFallsBackToHttpsBeforeReportingFailure()
    {
        Directory.CreateDirectory(_root);
        var progress = new RecordingProgress();
        var result = await ArchiveCloner.CloneAsync(
            _root, "ssh://git@127.0.0.1:1/owner/repo.git", "main",
            Path.Combine(_root, "clone"), progress, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("改用 HTTPS 后仍然失败", result.Message);
        Assert.Contains(progress.Lines, line => line.Contains("改用 HTTPS 重试"));
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
