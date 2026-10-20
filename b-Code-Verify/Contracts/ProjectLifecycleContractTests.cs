using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using HistoryJanus.Git;
using HistoryJanus.GitHub;
using HistoryJanus.Module;
using HistoryVulcan.Core.Commands;
using Xunit;

namespace HistoryJanus.Contracts;

public sealed class ProjectLifecycleContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HistoryJanus-Lifecycle", Guid.NewGuid().ToString("N"));
    private readonly string _data = Path.Combine(Path.GetTempPath(), "HistoryJanus-LifecycleData", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PushActionUpdatesCachedRowBeforeAutomaticRefresh(bool succeeds)
    {
        Directory.CreateDirectory(_root);
        var remote = Path.Combine(_root, "remote.git");
        const string name = "2026-902-AutoState";
        var project = Path.Combine(_root, name);
        await Git(_root, "init", "--bare", remote);
        Directory.CreateDirectory(project);
        await Git(project, "init", "-b", "main");
        await Git(project, "config", "user.name", "Janus Test");
        await Git(project, "config", "user.email", "janus@example.invalid");
        await Git(project, "commit", "--allow-empty", "-m", "initial");
        await Git(project, "remote", "add", "origin", remote);
        await Git(project, "push", "-u", "origin", "main");
        await Git(project, "commit", "--allow-empty", "-m", "next");
        var service = CreateService();
        var initial = (await service.ReadLifecycleStatusesAsync([new WorktreeInfo(name, project)], false))[0];
        Assert.Equal("推送", initial.LifecycleAction);
        var registry = new TestRegistrar();
        var bus = new TestCommandBus(registry);
        var composition = new StudioBusinessComposition(service, null!, null!, null!, null!, null!, null!, null!);
        HistoryJanusUiCommands.Register(registry, bus, "test", () => composition);
        var lists = 0;
        registry.Register(new CommandDescriptor
        {
            Name = "janus.proj.list", Domain = "janus", CommandClass = "proj", Summary = "test", Readonly = true,
            Parameters = [new() { Name = "status", Description = "test" }, new() { Name = "refresh", Description = "test" }],
            Handler = CommandDescriptor.Sync(_ =>
            {
                lists++;
                return CommandResult.Ok("rows", new[] { initial, new WorktreeInfo("2026-903-Unchanged", "untouched", LifecycleAction: "提交") });
            }),
        });
        registry.Register(new CommandDescriptor
        {
            Name = "janus.proj.push", Domain = "janus", CommandClass = "proj", Summary = "test", Readonly = true,
            Parameters = [new() { Name = "name", Description = "test" }],
            Handler = async _ =>
            {
                if (!succeeds) return CommandResult.Fail("test push failure");
                await Git(project, "push", "origin", "main");
                return CommandResult.Ok("pushed");
            },
        });
        string? displayed = null;
        registry.Register(new CommandDescriptor
        {
            Name = "aurora.ui.refreshdata", Domain = "aurora", CommandClass = "ui", Summary = "test", Readonly = true,
            Parameters = [new() { Name = "node", Description = "test" }],
            Handler = async _ =>
            {
                var data = await bus.ExecuteAsync("janus.ui.data view=projects", "UI");
                Assert.True(data.Success, data.Message);
                displayed = data.Message;
                return CommandResult.Ok("refreshed");
            },
        });
        HistoryJanusUiCommands.RequestProjectRefresh();
        Assert.True((await bus.ExecuteAsync("janus.ui.data view=projects", "UI")).Success);
        var result = await bus.ExecuteAsync($"janus.ui.projectaction name={name} action=推送", "UI");
        Assert.Equal(succeeds, result.Success);
        Assert.Equal(1, lists); // 操作结束没有全库扫描。
        // 5.15.3：失败也回写这一行并让表格重取——每条出路都要对上现场。
        Assert.NotNull(displayed);
        using var json = JsonDocument.Parse(displayed!);
        var row = json.RootElement.EnumerateArray().Single(item => item.GetProperty("name").GetString() == name);
        Assert.Equal(succeeds ? "同步 ↔" : "推送 ↑", row.GetProperty("status").GetString());
        var other = json.RootElement.EnumerateArray().Single(item => item.GetProperty("name").GetString() == "2026-903-Unchanged");
        Assert.Equal("提交 ●", other.GetProperty("status").GetString());
    }

    /// <summary>
    /// 「幽灵修改」：eol=crlf 文件的索引尺寸是 LF 版的，status 报 M、git diff HEAD 为空。
    /// 刷新要把它认成干净（并清掉索引里的旧尺寸），否则操作列永远是「提交」，点下去又说没有可提交的。
    /// 真有改动的文件不能被这条路吞掉。
    /// </summary>
    [Fact]
    public async Task StatOnlyModificationIsHealedAndDoesNotAskForCommit()
    {
        Directory.CreateDirectory(_root);
        var service = CreateService();
        const string projectName = "2026-905-Phantom";
        var project = Path.Combine(_root, projectName);
        Directory.CreateDirectory(project);
        await Git(project, "init", "-b", "main");
        await Git(project, "config", "user.name", "Janus Test");
        await Git(project, "config", "user.email", "janus@example.invalid");
        await Git(project, "config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(project, "a.cs"), "a\nb\n");
        await Git(project, "add", "-A");
        await Git(project, "commit", "-m", "lf");
        File.WriteAllText(Path.Combine(project, ".gitattributes"), "*.cs text eol=crlf\n");
        await Git(project, "add", "-A");
        await Git(project, "commit", "-m", "attr");
        File.WriteAllText(Path.Combine(project, "a.cs"), "a\r\nb\r\n");

        var healed = await service.RefreshProjectAsync(projectName);
        Assert.NotEqual(ProjectLifecycleState.Dirty, healed.State);
        Assert.Equal("推送", healed.Action); // 没有 origin：下一步是推送
        var staged = await GitRunner.RunAsync(project, ["diff", "--cached", "--name-only"]);
        Assert.Equal("", staged.Output.Trim());

        File.WriteAllText(Path.Combine(project, "a.cs"), "a\r\nchanged\r\n");
        var dirty = await service.RefreshProjectAsync(projectName);
        Assert.Equal(ProjectLifecycleState.Dirty, dirty.State);
        Assert.Equal("提交", dirty.Action);
    }

    /// <summary>
    /// gitlink 路径里只有打不开、也不会进本次提交的未跟踪文件时，git diff HEAD 是空的。
    /// 操作列要按这个结果去到下一步，不能被 status 的 M 压在「提交」上。
    /// 父仓库自己的新文件仍然要停在「提交」。
    /// </summary>
    [Fact]
    public async Task EmptyGitDiffAdvancesPastSubmoduleStatus()
    {
        Directory.CreateDirectory(_root);
        var service = CreateService();
        const string projectName = "2026-906-Gitlink";
        var project = Path.Combine(_root, projectName);
        var child = Path.Combine(project, "child");
        Directory.CreateDirectory(project);
        await Git(project, "init", "-b", "main");
        await Git(project, "config", "user.name", "Janus Test");
        await Git(project, "config", "user.email", "janus@example.invalid");
        await Git(project, "commit", "--allow-empty", "-m", "parent");
        Directory.CreateDirectory(child);
        await Git(child, "init", "-b", "main");
        await Git(child, "config", "user.name", "Janus Test");
        await Git(child, "config", "user.email", "janus@example.invalid");
        File.WriteAllText(Path.Combine(child, "a.txt"), "a\n");
        await Git(child, "add", "-A");
        await Git(child, "commit", "-m", "child");
        var sha = (await GitRunner.RunAsync(child, ["rev-parse", "HEAD"])).Output.Trim();
        await Git(project, "update-index", "--add", "--cacheinfo", $"160000,{sha},child");
        await Git(project, "commit", "-m", "link");
        var weird = Path.Combine(child, "only-untracked.txt");
        File.WriteAllText(weird, "u\n");

        var advanced = await service.RefreshProjectAsync(projectName);
        Assert.NotEqual("提交", advanced.Action);
        Assert.Equal("推送", advanced.Action);

        File.WriteAllText(Path.Combine(project, "new.txt"), "parent\n");
        var dirty = await service.RefreshProjectAsync(projectName);
        Assert.Equal("提交", dirty.Action);
    }

    /// <summary>目录里打不开的文件名不能让大小扫描抛异常，否则提交整单失败。</summary>
    [Fact]
    public void UnreadableDirectoryEntryDoesNotAbortSizeScan()
    {
        var root = Path.Combine(_root, "scan");
        Directory.CreateDirectory(root);
        var weird = @"\\?\" + Path.Combine(root, "psk.key.");
        using (var stream = new FileStream(weird, FileMode.Create, FileAccess.Write, FileShare.Read))
            stream.WriteByte((byte)'x');

        var scanned = WorktreeFileScanner.ScanDirectory(root, 1, 1024L * 1024 * 1024);
        Assert.DoesNotContain(scanned.LargeFiles, file => file.RelativePath.Contains("psk.key", StringComparison.Ordinal));
    }

    /// <summary>
    /// 远端连不上时「操作」格是「刷新」，不是「同步」。
    /// 推送后的缓存回写也不能拿陈旧的远端引用把它算回去。
    /// </summary>
    [Fact]
    public async Task UnreachableRemoteAsksForRefreshInsteadOfSync()
    {
        Directory.CreateDirectory(_root);
        var remote = Path.Combine(_root, "remote.git");
        var projectName = "2026-904-Offline";
        var project = Path.Combine(_root, projectName);
        await Git(_root, "init", "--bare", remote);
        Directory.CreateDirectory(project);
        await Git(project, "init", "-b", "main");
        await Git(project, "config", "user.name", "Janus Test");
        await Git(project, "config", "user.email", "janus@example.invalid");
        File.WriteAllText(Path.Combine(project, "readme.txt"), "tracked");
        await Git(project, "add", "-A");
        await Git(project, "commit", "-m", "initial");
        await Git(project, "remote", "add", "origin", remote);
        await Git(project, "push", "-u", "origin", "main");
        await Git(project, "remote", "set-url", "origin", Path.Combine(_root, "missing.git"));

        var service = CreateService();
        var offline = await service.RefreshProjectAsync(projectName);
        Assert.Equal(ProjectLifecycleState.Unavailable, offline.State);
        Assert.Equal(ProjectService.RefreshAction, offline.Action);

        // 不 fetch 重算会得到「同步」；带着已取到的快照回写才保住「刷新」。
        var stale = await service.RefreshProjectAsync(projectName, fetchRemote: false);
        Assert.Equal("同步", stale.Action);
        var row = ProjectService.ApplyLifecycle(new WorktreeInfo(projectName, project), offline);
        Assert.Null(row.IsClean);
        using var json = JsonDocument.Parse(HistoryJanusUiCommands.UiProjectProjection.Serialize(new[] { row }));
        Assert.Equal("刷新 ?", json.RootElement[0].GetProperty("status").GetString());
        Assert.Equal("刷新", json.RootElement[0].GetProperty("lifecycleAction").GetString());
    }

    [Fact]
    public async Task LifecyclePreservesMultipleZFoldersAndRestoresThemOverRemoteContent()
    {
        Directory.CreateDirectory(_root);
        var remote = Path.Combine(_root, "remote.git");
        var projectName = "2026-901-Lifecycle";
        var project = Path.Combine(_root, projectName);
        await Git(_root, "init", "--bare", remote);
        Directory.CreateDirectory(project);
        await Git(project, "init", "-b", "main");
        await Git(project, "config", "user.name", "Janus Test");
        await Git(project, "config", "user.email", "janus@example.invalid");
        Directory.CreateDirectory(Path.Combine(project, "z-one"));
        Directory.CreateDirectory(Path.Combine(project, "Z-two"));
        File.WriteAllText(Path.Combine(project, "z-one", "local.txt"), "remote-version");
        File.WriteAllText(Path.Combine(project, "Z-two", "keep.txt"), "keep");
        File.WriteAllText(Path.Combine(project, "readme.txt"), "tracked");
        await Git(project, "add", "-A");
        await Git(project, "commit", "-m", "initial");
        await Git(project, "remote", "add", "origin", remote);
        await Git(project, "push", "-u", "origin", "main");

        var service = CreateService();
        var initial = await service.RefreshProjectAsync(projectName);
        Assert.Equal(ProjectLifecycleState.Unverified, initial.State);
        Assert.Equal("同步", initial.Action);
        var sync = await service.SyncAsync(projectName);
        Assert.True(sync.Success, sync.Message);
        Assert.Equal(ProjectLifecycleState.Synchronized, sync.Snapshot!.State);
        Assert.Equal("归档", sync.Snapshot.Action);

        var archive = await service.ArchiveAsync(projectName);
        Assert.True(archive.Success, archive.Message);
        Assert.False(Directory.Exists(Path.Combine(project, ".git")));
        Assert.Equal(new[] { "z-one", "Z-two" }, Directory.EnumerateDirectories(project)
            .Select(Path.GetFileName).OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray());

        File.WriteAllText(Path.Combine(project, "z-one", "local.txt"), "local-wins");
        var archived = await service.RefreshProjectAsync(projectName);
        Assert.Equal(ProjectLifecycleState.Archived, archived.State);
        Assert.Equal("拉取", archived.Action);

        var pull = await service.PullAsync(projectName);
        Assert.True(pull.Success, pull.Message);
        Assert.True(Directory.Exists(Path.Combine(project, ".git")));
        Assert.Equal("local-wins", File.ReadAllText(Path.Combine(project, "z-one", "local.txt")));
        var restored = await service.RefreshProjectAsync(projectName);
        Assert.Equal(ProjectLifecycleState.Dirty, restored.State);
        Assert.Equal("提交", restored.Action);
    }

    [Fact]
    public async Task StatusPriorityCoversNoOriginAheadBehindAndDiverged()
    {
        Directory.CreateDirectory(_root);
        var service = CreateService();
        var projectName = "2026-902-States";
        var project = Path.Combine(_root, projectName);
        Directory.CreateDirectory(project);
        await Git(project, "init", "-b", "main");
        await Git(project, "config", "user.name", "Janus Test");
        await Git(project, "config", "user.email", "janus@example.invalid");
        File.WriteAllText(Path.Combine(project, "file.txt"), "one");
        await Git(project, "add", "-A");
        await Git(project, "commit", "-m", "one");
        Assert.Equal(ProjectLifecycleState.NoRemote, (await service.RefreshProjectAsync(projectName)).State);

        var remote = Path.Combine(_root, "states.git");
        await Git(_root, "init", "--bare", remote);
        await Git(project, "remote", "add", "origin", remote);
        await Git(project, "push", "-u", "origin", "main");
        await service.SyncAsync(projectName);
        File.AppendAllText(Path.Combine(project, "file.txt"), "two");
        await Git(project, "add", "-A");
        await Git(project, "commit", "-m", "local-ahead");
        Assert.Equal(ProjectLifecycleState.Ahead, (await service.RefreshProjectAsync(projectName)).State);

        var peer = Path.Combine(_root, "peer");
        await Git(_root, "clone", "--branch", "main", remote, peer);
        await Git(peer, "config", "user.name", "Janus Peer");
        await Git(peer, "config", "user.email", "peer@example.invalid");
        File.WriteAllText(Path.Combine(peer, "peer.txt"), "remote");
        await Git(peer, "add", "-A");
        await Git(peer, "commit", "-m", "remote-ahead");
        await Git(peer, "push", "origin", "main");
        var diverged = await service.RefreshProjectAsync(projectName);
        Assert.Equal(ProjectLifecycleState.Diverged, diverged.State);
        Assert.Equal("同步", diverged.Action);

        await Git(project, "reset", "--hard", "origin/main");
        File.WriteAllText(Path.Combine(peer, "peer2.txt"), "remote2");
        await Git(peer, "add", "-A");
        await Git(peer, "commit", "-m", "remote-behind");
        await Git(peer, "push", "origin", "main");
        var behind = await service.RefreshProjectAsync(projectName);
        Assert.Equal(ProjectLifecycleState.Behind, behind.State);
    }

    /// <summary>
    /// 5.16.4：工作树在别的分支上时，推送推的是那条分支，判断也拿它比 origin 上的同名分支。
    /// 此前一律比 origin/main，推上去之后操作列仍是「推送」。同步只对 main 做，在分支上被拒绝。
    /// </summary>
    [Fact]
    public async Task SideBranchIsComparedWithItsOwnRemoteBranch()
    {
        Directory.CreateDirectory(_root);
        var service = CreateService();
        var projectName = "2026-903-Side";
        var project = Path.Combine(_root, projectName);
        Directory.CreateDirectory(project);
        await Git(project, "init", "-b", "main");
        await Git(project, "config", "user.name", "Janus Test");
        await Git(project, "config", "user.email", "janus@example.invalid");
        File.WriteAllText(Path.Combine(project, "file.txt"), "one");
        await Git(project, "add", "-A");
        await Git(project, "commit", "-m", "one");
        var remote = Path.Combine(_root, "side.git");
        await Git(_root, "init", "--bare", remote);
        await Git(project, "remote", "add", "origin", remote);
        await Git(project, "push", "-u", "origin", "main");

        await Git(project, "switch", "-c", "feature");
        File.WriteAllText(Path.Combine(project, "feature.txt"), "f");
        await Git(project, "add", "-A");
        await Git(project, "commit", "-m", "feature work");
        var unpushed = await service.RefreshProjectAsync(projectName);
        Assert.Equal("推送", unpushed.Action);
        Assert.Contains("feature", unpushed.Message);

        var pushed = await service.PushAsync(projectName);
        Assert.True(pushed.Success, pushed.Message);
        var after = await service.RefreshProjectAsync(projectName, fetchRemote: false);
        Assert.NotEqual("推送", after.Action);
        Assert.Equal(ProjectService.RefreshAction, after.Action);
        Assert.False((await service.SyncAsync(projectName)).Success);

        // 回到 main：照旧按 main 判断。
        await Git(project, "switch", "main");
        Assert.NotEqual("推送", (await service.RefreshProjectAsync(projectName)).Action);
    }

    [Fact]
    public async Task ArchivedHistoryPaginatesGitHubAndRejectsUnsupportedOrUnauthenticatedRemotes()
    {
        Directory.CreateDirectory(_root);
        using var reader = new GitHubRemoteHistoryReader(
            new HistoryHandler(), (_, _) => Task.FromResult<string?>("test-token"), "https://example.invalid/");
        var record = new ProjectLifecycleRecord("2026-903-History", "git@github.com:owner/repo.git",
            "main", new string('a', 40), [], DateTimeOffset.Now);
        var result = await reader.ReadAsync(record, _root, 101, 0, CancellationToken.None);
        Assert.True(result.Success, result.Message);
        Assert.Equal(101, result.Report!.Entries.Count);

        var unsupported = await reader.ReadAsync(record with { Remote = "https://gitlab.com/owner/repo.git" },
            _root, 10, 0, CancellationToken.None);
        Assert.False(unsupported.Success);
        Assert.Contains("不是 GitHub", unsupported.Message);

        using var unauthorized = new GitHubRemoteHistoryReader(
            new StatusHandler(HttpStatusCode.Unauthorized), (_, _) => Task.FromResult<string?>("bad"),
            "https://example.invalid/");
        var denied = await unauthorized.ReadAsync(record, _root, 10, 0, CancellationToken.None);
        Assert.False(denied.Success);
        Assert.Contains("认证失败", denied.Message);
    }

    private ProjectService CreateService()
    {
        var settings = new MemorySettings();
        settings.Set(ProjectService.KeyLibraryRoot, _root);
        return new ProjectService(settings, _ => true, _data);
    }

    private static async Task Git(string directory, params string[] args)
    {
        var result = await GitRunner.RunAsync(directory, args);
        Assert.True(result.Success, $"git {string.Join(' ', args)} failed: {result.Output}");
    }

    public void Dispose()
    {
        TryDelete(_root);
        TryDelete(_data);
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) ProjectRepoLayout.DeleteTree(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public int GetInt(string key, int fallback) => int.TryParse(Get(key), out var value) ? value : fallback;
        public void Set(string key, string value) => _values[key] = value;
        public IReadOnlyList<KeyValuePair<string, string>> All() => _values.ToList();
    }

    private sealed class HistoryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var second = request.RequestUri!.Query.Contains("page=2", StringComparison.Ordinal);
            var count = second ? 1 : 100;
            var offset = second ? 100 : 0;
            var rows = Enumerable.Range(offset, count).Select(index => new
            {
                sha = index.ToString("x40"),
                commit = new
                {
                    author = new { name = "tester", date = DateTimeOffset.UtcNow.AddMinutes(-index) },
                    message = $"commit {index}",
                },
                parents = index == 100 ? Array.Empty<object>() : new object[] { new { sha = "parent" } },
            });
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(rows), Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status));
    }
}
