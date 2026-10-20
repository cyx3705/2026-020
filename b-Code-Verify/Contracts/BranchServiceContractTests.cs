using System.IO;
using HistoryJanus.Git;
using Xunit;

namespace HistoryJanus.Contracts;

/// <summary>
/// 分支控制（5.16.0）在真 git 仓上的行为：分出不切换、脏工作树拒绝切换与合并、
/// 有冲突的合并整个退回、当前分支与 main 删不掉、未合并的要强制删、历史按所选分支取。
/// </summary>
public sealed class BranchServiceContractTests : IDisposable
{
    private const string Name = "2026-907-Branches";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HistoryJanus-Branches", Guid.NewGuid().ToString("N"));
    private readonly string _data = Path.Combine(Path.GetTempPath(), "HistoryJanus-BranchesData", Guid.NewGuid().ToString("N"));

    private string Project => Path.Combine(_root, Name);

    [Fact]
    public async Task CreateSwitchMergeAndDeleteFollowTheWorktreeRules()
    {
        var (projects, branches) = await CreateAsync();

        // 分出：新分支出现，工作树不动。
        var created = await branches.CreateAsync(Name, "main", "feature");
        Assert.True(created.Success, created.Message);
        Assert.Equal("main", await branches.CurrentBranchAsync(Name));
        Assert.False((await branches.CreateAsync(Name, "main", "feature")).Success);
        Assert.False((await branches.CreateAsync(Name, "main", "bad name..")).Success);

        var listed = await branches.ListAsync(Name);
        Assert.True(listed.Success, listed.Message);
        Assert.Equal(new[] { "main", "feature" }, listed.Report!.Branches.Select(b => b.Name).ToArray());
        Assert.True(listed.Report.Branches[0].IsCurrent);

        // 脏工作树：切换被拒，并标出 Dirty（界面据此弹提交窗口）。
        File.WriteAllText(Path.Combine(Project, "file.txt"), "dirty");
        var refused = await branches.SwitchAsync(Name, "feature");
        Assert.False(refused.Success);
        Assert.True(refused.Dirty);
        Assert.Equal("main", await branches.CurrentBranchAsync(Name));
        await Git(Project, "checkout", "--", "file.txt");

        // 干净时切过去；当前分支删不掉，main 也删不掉。
        Assert.True((await branches.SwitchAsync(Name, "feature")).Success);
        Assert.Equal("feature", await branches.CurrentBranchAsync(Name));
        Assert.False((await branches.DeleteAsync(Name, "feature", force: false)).Success);
        Assert.False((await branches.DeleteAsync(Name, "main", force: true)).Success);

        // 在 feature 上提交，回 main 合并进来。
        File.WriteAllText(Path.Combine(Project, "feature.txt"), "f");
        await Git(Project, "add", "-A");
        await Git(Project, "commit", "-m", "feature work");
        Assert.True((await branches.SwitchAsync(Name, "main")).Success);
        Assert.False(await branches.IsMergedAsync(Name, "feature"));
        Assert.False((await branches.DeleteAsync(Name, "feature", force: false)).Success);
        // 未合并时分支控制行摆着它；合并进 main 之后就不再摆（5.16.1），当前分支始终在。
        var unmerged = (await branches.ListAsync(Name)).Report!;
        Assert.Equal(new[] { "main", "feature" },
            HistoryJanus.Module.HistoryJanusUiCommands.UiBranchProjection.Options(unmerged).Select(row => row["value"]));
        Assert.Equal("main · 当前", HistoryJanus.Module.HistoryJanusUiCommands.UiBranchProjection.Options(unmerged)[0]["text"]);
        var merged = await branches.MergeAsync(Name, "feature");
        Assert.True(merged.Success, merged.Message);
        Assert.True(File.Exists(Path.Combine(Project, "feature.txt")));
        Assert.True(await branches.IsMergedAsync(Name, "feature"));
        var afterMerge = (await branches.ListAsync(Name)).Report!;
        Assert.True(afterMerge.Branches.Single(b => b.Name == "feature").MergedIntoMain);
        Assert.Equal(new[] { "main" },
            HistoryJanus.Module.HistoryJanusUiCommands.UiBranchProjection.Options(afterMerge).Select(row => row["value"]));
        Assert.True((await branches.DeleteAsync(Name, "feature", force: false)).Success);
        Assert.Single((await branches.ListAsync(Name)).Report!.Branches);
        _ = projects;
    }

    [Fact]
    public async Task AConflictingMergeLeavesEverythingAsItWas()
    {
        var (_, branches) = await CreateAsync();
        Assert.True((await branches.CreateAsync(Name, "main", "other")).Success);
        File.WriteAllText(Path.Combine(Project, "file.txt"), "main side");
        await Git(Project, "commit", "-am", "main side");
        Assert.True((await branches.SwitchAsync(Name, "other")).Success);
        File.WriteAllText(Path.Combine(Project, "file.txt"), "other side");
        await Git(Project, "commit", "-am", "other side");
        Assert.True((await branches.SwitchAsync(Name, "main")).Success);
        var head = (await GitRunner.RunAsync(Project, ["rev-parse", "HEAD"])).Output.Trim();

        var merged = await branches.MergeAsync(Name, "other");

        Assert.False(merged.Success);
        Assert.Equal(head, (await GitRunner.RunAsync(Project, ["rev-parse", "HEAD"])).Output.Trim());
        Assert.Equal("", (await GitRunner.RunAsync(Project, ["status", "--porcelain"])).Output.Trim());
        Assert.Equal("main side", File.ReadAllText(Path.Combine(Project, "file.txt")));
    }

    [Fact]
    public async Task HistoryReadsTheRequestedBranch()
    {
        var (projects, branches) = await CreateAsync();
        Assert.True((await branches.CreateAsync(Name, "main", "side")).Success);
        Assert.True((await branches.SwitchAsync(Name, "side")).Success);
        File.WriteAllText(Path.Combine(Project, "side.txt"), "s");
        await Git(Project, "add", "-A");
        await Git(Project, "commit", "-m", "only on side");

        var history = new BranchHistoryService(projects);
        var main = await history.GetHistoryAsync(Name);
        var side = await history.GetHistoryAsync(Name, branch: "side");

        Assert.True(main.Success, main.Message);
        Assert.True(side.Success, side.Message);
        Assert.DoesNotContain(main.Report!.Entries, entry => entry.Subject == "only on side");
        Assert.Contains(side.Report!.Entries, entry => entry.Subject == "only on side");
        Assert.False((await history.GetHistoryAsync(Name, branch: "missing")).Success);
    }

    private async Task<(ProjectService Projects, BranchService Branches)> CreateAsync()
    {
        Directory.CreateDirectory(Project);
        await Git(Project, "init", "-b", "main");
        await Git(Project, "config", "user.name", "Janus Test");
        await Git(Project, "config", "user.email", "janus@example.invalid");
        File.WriteAllText(Path.Combine(Project, "file.txt"), "one");
        await Git(Project, "add", "-A");
        await Git(Project, "commit", "-m", "one");

        var settings = new MemorySettings();
        settings.Set(ProjectService.KeyLibraryRoot, _root);
        var projects = new ProjectService(settings, _ => true, _data);
        return (projects, new BranchService(projects));
    }

    private static async Task Git(string directory, params string[] args)
    {
        var result = await GitRunner.RunAsync(directory, args);
        Assert.True(result.Success, $"git {string.Join(' ', args)} failed: {result.Output}");
    }

    public void Dispose()
    {
        foreach (var path in new[] { _root, _data })
        {
            try { if (Directory.Exists(path)) ProjectRepoLayout.DeleteTree(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public int GetInt(string key, int fallback) => int.TryParse(Get(key), out var value) ? value : fallback;
        public void Set(string key, string value) => _values[key] = value;
        public IReadOnlyList<KeyValuePair<string, string>> All() => _values.ToList();
    }
}
