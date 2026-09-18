using HistoryVulcan.Core.Commands;
using HistoryJanus.Git;
using static HistoryJanus.Smoke.SmokeKit;

namespace HistoryJanus.Smoke.Suites;

/// <summary>
/// 唯一的规则形态：全库共用的「不纳入仓库」清单，以及它在提交链路里的自动落地。
/// LFS 不在规则面内——只在提交链路对超过 GitHub 100MB 硬限的具体文件征求同意（DEC-022）。
/// </summary>
internal static class GitRulesSuite
{
    public static async Task RunAsync(string[] args)
    {
        var temp = TemporaryDirectory("gitrules");
        try
        {
            TestParsing(temp);
            await TestIgnoreBlockIsIdempotentAsync(temp);
            await TestBlockPreservesHandwrittenContentAsync(temp);
            await TestCommitAppliesRulesAutomaticallyAsync(temp);
            await TestSnapshotFilesStayTrackedAsync(temp);
            await TestCommandsRegisteredAsync(temp);
        }
        finally
        {
            DeleteTree(temp);
        }
    }

    private static void TestParsing(string temp)
    {
        var service = BuildService(temp, out _);

        var bad = service.SetExcludeList("bin/, C:\\Windows, *.log");
        True(!bad.Success, "a windows path is rejected");
        Contains(bad.Message, "C:", "the rejection names the offending entry");
        Contains(service.RawExcludeList, "obj/",
            "a rejected list leaves the previous list untouched");

        True(!service.SetExcludeList("   ").Success, "an empty list is rejected");

        var ok = service.SetExcludeList("obj/, bin/, *.log, Thumbs.db, tools/jdk/");
        True(ok.Success, $"a valid list is accepted: {ok.Message}");
        True(service.Directories.SequenceEqual(["obj/", "bin/", "tools/jdk/"]),
            "directories keep their order and trailing slash");
        True(service.Suffixes.SequenceEqual(["*.log", "Thumbs.db"]),
            "suffixes accept both *.ext and a bare filename");

        True(!service.SetExcludeList("bin/, sub/dir").Success,
            "a path without a trailing slash is not a valid entry");
    }

    private static async Task TestIgnoreBlockIsIdempotentAsync(string temp)
    {
        var root = Path.Combine(temp, "idempotent");
        Directory.CreateDirectory(root);
        var service = BuildService(temp, out _);
        service.SetExcludeList("bin/, obj/, tools/jdk/, *.log");

        var first = await service.EnsureIgnoreAsync(root);
        True(first is { Success: true, Changed: true }, "the first write creates the managed block");
        var afterFirst = await File.ReadAllTextAsync(Path.Combine(root, ".gitignore"));

        var second = await service.EnsureIgnoreAsync(root);
        True(second is { Success: true, Changed: false }, "a second write reports no change");
        Equal(afterFirst, await File.ReadAllTextAsync(Path.Combine(root, ".gitignore")),
            "an idempotent write leaves the file byte-identical");

        // 嵌套目录必须带 **/ 前缀：不带前缀时 git 只按仓库根匹配，
        // a17-xxx/tools/jdk/ 会漏掉（2026-08 全库推送实测踩过）。
        Contains(afterFirst, "**/tools/jdk/", "a nested directory entry is prefixed for any depth");
        True(!afterFirst.Contains("**/bin/", StringComparison.Ordinal),
            "a single-segment directory needs no prefix");
        True(!afterFirst.Contains("filter=lfs", StringComparison.Ordinal),
            "the managed block never emits LFS attributes");
    }

    /// <summary>
    /// 正式消费快照豁免：z-* 下的产物由发布管线刻意入库，跨项目消费依赖它们。
    /// 清单里写了 *.dll 之类不得把这些删出索引——这条最容易在将来被顺手删掉，
    /// 所以用真实 git 仓库断言「排除生效但快照仍被跟踪」。
    /// </summary>
    private static async Task TestSnapshotFilesStayTrackedAsync(string temp)
    {
        var root = Path.Combine(temp, "snapshot-library");
        var project = Path.Combine(root, "2026-241-Snapshot");
        Directory.CreateDirectory(root);
        await InitStandaloneRepo(project, "Rules Smoke", "rules@example.invalid");
        await CommitFile(project, "README.md", "seed\n", "seed");

        Directory.CreateDirectory(Path.Combine(project, "z-HistoryDemo", "host"));
        Directory.CreateDirectory(Path.Combine(project, "b-Code", "bin"));
        await File.WriteAllTextAsync(
            Path.Combine(project, "z-HistoryDemo", "Demo.dll"), "formal snapshot");
        await File.WriteAllTextAsync(
            Path.Combine(project, "z-HistoryDemo", "host", "Host.dll"), "formal host");
        await File.WriteAllTextAsync(
            Path.Combine(project, "b-Code", "bin", "Build.dll"), "build output");
        await File.WriteAllTextAsync(Path.Combine(project, "loose.dll"), "loose build output");
        await File.WriteAllTextAsync(Path.Combine(project, "src.cs"), "// source");

        var service = BuildService(temp, out var settings, root);
        service.SetExcludeList("bin/, *.dll");
        var projects = new ProjectService(settings, _ => true, temp) { ExcludeRules = service };
        var report = await projects.CommitAsync("2026-241-Snapshot", "快照豁免", null);
        True(report.Outcome == CommitOutcome.Success, $"commit succeeds: {report.Message}");

        var tracked = Run(project, ["ls-files"]);
        Contains(tracked, "z-HistoryDemo/Demo.dll",
            "a formal snapshot dll stays tracked despite *.dll being excluded");
        Contains(tracked, "z-HistoryDemo/host/Host.dll",
            "the exemption reaches nested snapshot directories");
        Contains(tracked, "src.cs", "ordinary source is unaffected");
        True(!tracked.Contains("loose.dll", StringComparison.Ordinal),
            "a dll outside the snapshot is excluded");
        True(!tracked.Contains("Build.dll", StringComparison.Ordinal),
            "build output under an excluded directory stays excluded");

        DeleteTree(root);
    }

    private static async Task TestBlockPreservesHandwrittenContentAsync(string temp)
    {
        var root = Path.Combine(temp, "preserve");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, ".gitignore");
        await File.WriteAllTextAsync(path, "# 我自己写的\n/secret-local/\n");

        var service = BuildService(temp, out _);
        service.SetExcludeList("bin/, *.log");
        await service.EnsureIgnoreAsync(root);
        var text = await File.ReadAllTextAsync(path);
        Contains(text, "# 我自己写的", "handwritten comments survive");
        Contains(text, "/secret-local/", "handwritten entries survive");
        Contains(text, "bin/", "the managed block is appended");

        service.SetExcludeList("obj/, *.tmp");
        await service.EnsureIgnoreAsync(root);
        var updated = await File.ReadAllTextAsync(path);
        Contains(updated, "/secret-local/", "a list change preserves content outside the block");
        Contains(updated, "*.tmp", "the block reflects the new list");
        True(!updated.Contains("*.log", StringComparison.Ordinal),
            "the block drops entries removed from the list");
        Equal(1, CountOccurrences(updated, "# HistoryJanus managed begin"),
            "repeated writes never duplicate the managed block");
    }

    private static async Task TestCommitAppliesRulesAutomaticallyAsync(string temp)
    {
        var root = Path.Combine(temp, "library");
        var project = Path.Combine(root, "2026-240-Rules");
        Directory.CreateDirectory(root);
        await InitStandaloneRepo(project, "Rules Smoke", "rules@example.invalid");
        await CommitFile(project, "README.md", "seed\n", "seed");

        var service = BuildService(temp, out var settings, root);
        service.SetExcludeList("bin/, *.log");
        var projects = new ProjectService(settings, _ => true, temp) { ExcludeRules = service };

        Directory.CreateDirectory(Path.Combine(project, "bin"));
        await File.WriteAllTextAsync(Path.Combine(project, "bin", "app.exe"), "binary");
        await File.WriteAllTextAsync(Path.Combine(project, "noise.log"), "log");
        await File.WriteAllTextAsync(Path.Combine(project, "keep.txt"), "kept");

        // 提交链路自己刷规则：没有任何手动下发步骤
        var report = await projects.CommitAsync("2026-240-Rules", "自动落地规则", null);
        True(report.Outcome == CommitOutcome.Success, $"commit succeeds: {report.Message}");

        var tracked = Run(project, ["ls-files"]);
        Contains(tracked, "keep.txt", "a normal file is committed");
        True(!tracked.Contains("app.exe", StringComparison.Ordinal),
            "an excluded directory never enters the index");
        True(!tracked.Contains("noise.log", StringComparison.Ordinal),
            "an excluded suffix never enters the index");
        Contains(tracked, ".gitignore", "the refreshed .gitignore itself is committed");
    }

    private static async Task TestCommandsRegisteredAsync(string temp)
    {
        var service = BuildService(temp, out _);
        var registry = new CommandRegistry();
        GitRuleCommands.RegisterAll(registry, service, "module:HistoryJanus");

        var names = registry.All().Select(descriptor => descriptor.Name)
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        // 5.9.0 增加 janus.gitrule.lfs：规则面的 LFS 行改为列本仓实际走 LFS 的文件，
        // 事实来自 git lfs ls-files，因此它是一条按项目取的只读命令。
        True(names.SequenceEqual(["janus.gitrule.excludes", "janus.gitrule.lfs", "janus.gitrule.list"]),
            $"gitrule exposes exactly three commands, was: {string.Join(", ", names)}");

        True(registry.TryGet("janus.gitrule.list", out var list) && list.Readonly,
            "janus.gitrule.list stays readonly");
        True(registry.TryGet("janus.gitrule.lfs", out var lfs) && lfs.Readonly,
            "janus.gitrule.lfs stays readonly");
        True(registry.TryGet("janus.gitrule.excludes", out var excludes)
             && excludes.ConfirmPrompt != null,
            "changing the shared list requires confirmation");

        foreach (var retired in new[]
                 {
                     "janus.gitrule.set", "janus.gitrule.batchset", "janus.gitrule.remove",
                     "janus.gitrule.sync", "janus.gitrule.scan", "janus.gitrule.review",
                 })
        {
            True(!registry.TryGet(retired, out _), $"{retired} is retired without an alias");
        }

        var bus = new CommandBus(registry, new MemoryLog());
        True((await bus.ExecuteAsync("janus.gitrule.list", "Smoke")).Success,
            "janus.gitrule.list executes through the bus");
    }

    private static GitFileRuleService BuildService(
        string temp, out MemorySettings settings, string? libraryRoot = null)
    {
        settings = new MemorySettings();
        var root = libraryRoot ?? Path.Combine(temp, "empty-library");
        Directory.CreateDirectory(root);
        BindLibrary(settings, root);
        return new GitFileRuleService(new ProjectService(settings, _ => true, temp), settings);
    }

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }
        return count;
    }
}
