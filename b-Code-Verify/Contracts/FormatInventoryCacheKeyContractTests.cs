using System.Diagnostics;
using HistoryJanus.Git;
using Xunit;

namespace HistoryJanus.Contracts;

/// <summary>
/// 格式台账缓存键的索引时间戳合同。
/// </summary>
/// <remarks>
/// 一项目一仓（DEC-015）之后 <c>.git</c> 是目录，而原实现只处理 git worktree 的 <c>.git</c> 文件，
/// 导致索引戳恒为 noindex——缓存键再也感知不到暂存区变化，暂存但未提交时会命中过期缓存。
/// 两种仓形态都必须给出真实时间戳，且暂存后必须变化。
/// </remarks>
public sealed class FormatInventoryCacheKeyContractTests : IDisposable
{
    private readonly string _sandbox;

    public FormatInventoryCacheKeyContractTests()
        => _sandbox = Path.Combine(Path.GetTempPath(), "janus-cachekey-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public async Task IndependentRepoWithDirectoryGitYieldsRealStamp()
    {
        var repo = InitRepo("independent");

        var stamp = await FormatInventoryService.ReadIndexStampAsync(repo, default);

        Assert.NotEqual("noindex", stamp);
        Assert.True(Directory.Exists(Path.Combine(repo, ".git")), "本用例的前提是 .git 为目录");
    }

    [Fact]
    public async Task StagingChangesTheStampWithoutCommitting()
    {
        var repo = InitRepo("staging");
        var before = await FormatInventoryService.ReadIndexStampAsync(repo, default);
        var headBefore = Run(repo, "rev-parse", "HEAD");

        // 只暂存不提交：HEAD 不变，索引变。缓存键必须因此失效。
        File.WriteAllText(Path.Combine(repo, "added.txt"), "added");
        await Task.Delay(50);
        Run(repo, "add", "-A");

        var after = await FormatInventoryService.ReadIndexStampAsync(repo, default);

        Assert.Equal(headBefore, Run(repo, "rev-parse", "HEAD"));
        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task LinkedWorktreeWithFileGitStillResolves()
    {
        var repo = InitRepo("origin");
        var linked = Path.Combine(_sandbox, "linked");
        Run(repo, "worktree", "add", "-b", "side", linked, "HEAD");

        Assert.True(File.Exists(Path.Combine(linked, ".git")), "本用例的前提是 .git 为文件");
        var stamp = await FormatInventoryService.ReadIndexStampAsync(linked, default);

        Assert.NotEqual("noindex", stamp);
    }

    [Fact]
    public async Task NonRepositoryFallsBackToNoIndex()
    {
        var plain = Path.Combine(_sandbox, "plain");
        Directory.CreateDirectory(plain);

        Assert.Equal("noindex", await FormatInventoryService.ReadIndexStampAsync(plain, default));
    }

    private string InitRepo(string name)
    {
        var path = Path.Combine(_sandbox, name);
        Directory.CreateDirectory(path);
        Run(path, "init", "-b", "main");
        Run(path, "config", "user.email", "contract@test.local");
        Run(path, "config", "user.name", "contract");
        File.WriteAllText(Path.Combine(path, "seed.txt"), "seed");
        Run(path, "add", "-A");
        Run(path, "commit", "-m", "seed");
        return path;
    }

    private static string Run(string workingDirectory, params string[] arguments)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Trim();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_sandbox))
                Directory.Delete(_sandbox, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
