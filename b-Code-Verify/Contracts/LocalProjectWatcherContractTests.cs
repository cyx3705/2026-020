using System.IO;
using HistoryJanus.Git;
using HistoryJanus.Module;
using Xunit;

namespace HistoryJanus.Contracts;

/// <summary>
/// 5.16.2 本地改动无感刷新：文件改动折算成项目名；.git 里只认会改变本地读数的那几样，
/// 本地重读自己会写的 index、锁和对象不算，否则监视器会被自己触发、停不下来。
/// </summary>
public sealed class LocalProjectWatcherContractTests
{
    private const string Root = @"C:\Lib\";

    [Theory]
    [InlineData(@"C:\Lib\2026-020-Janus\src\a.cs", "2026-020-Janus")]
    [InlineData(@"C:\Lib\2026-020-Janus", "2026-020-Janus")]
    [InlineData(@"C:\Lib\2026-020-Janus\.git\HEAD", "2026-020-Janus")]
    [InlineData(@"C:\Lib\2026-020-Janus\.git\packed-refs", "2026-020-Janus")]
    [InlineData(@"C:\Lib\2026-020-Janus\.git\refs\heads\main", "2026-020-Janus")]
    [InlineData(@"C:\Lib\2026-020-Janus\.git\refs\heads\ai\x", "2026-020-Janus")]
    [InlineData(@"C:\Lib\2026-020-Janus\.git\index", null)]
    [InlineData(@"C:\Lib\2026-020-Janus\.git\index.lock", null)]
    [InlineData(@"C:\Lib\2026-020-Janus\.git\objects\ab\cdef", null)]
    [InlineData(@"C:\Lib\2026-020-Janus\.git\refs\heads\main.lock", null)]
    [InlineData(@"C:\Lib\2026-020-Janus\.git\refs\remotes\origin\main", null)]
    [InlineData(@"C:\Lib\2026-020-Janus\.git\FETCH_HEAD", null)]
    [InlineData(@"C:\Lib\2026-020-Janus\.git", null)]
    [InlineData(@"C:\Other\x.txt", null)]
    public void PathsMapToTheProjectWhoseLocalReadingMayHaveChanged(string path, string? expected)
        => Assert.Equal(expected, LocalProjectWatcher.ProjectOf(Root, path));

    [Fact]
    public void OnlyVisibleFieldsDecideWhetherTheOverviewRefetches()
    {
        var before = new WorktreeInfo("p", @"C:\Lib\p", IsClean: true, LifecycleAction: "推送", ZFolders: ["z-a"]);
        // 列表字段换了一个新实例但内容一样：不算变。
        Assert.True(HistoryJanusUiCommands.SameLocalReading(before, before with { ZFolders = ["z-a"] }));
        Assert.False(HistoryJanusUiCommands.SameLocalReading(before, before with { IsClean = false }));
        Assert.False(HistoryJanusUiCommands.SameLocalReading(before, before with { LifecycleAction = "提交" }));
    }

    [Fact]
    public async Task ChangesInsideAProjectAreReportedOnceAfterTheyStop()
    {
        var root = Path.Combine(Path.GetTempPath(), "janus-watch-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "2026-999-Watch");
        Directory.CreateDirectory(project);
        var reported = new List<string>();
        try
        {
            using var watcher = new LocalProjectWatcher(root, name =>
            {
                lock (reported) reported.Add(name);
                return Task.CompletedTask;
            });
            for (var i = 0; i < 5; i++)
                File.WriteAllText(Path.Combine(project, $"f{i}.txt"), "x");

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                lock (reported)
                    if (reported.Count > 0) break;
                await Task.Delay(50);
            }
            await Task.Delay(LocalProjectWatcher.Quiet * 2);
            lock (reported)
                Assert.Equal(new[] { "2026-999-Watch" }, reported);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
