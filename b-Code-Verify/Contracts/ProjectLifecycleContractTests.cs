using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using HistoryJanus.Git;
using HistoryJanus.GitHub;
using HistoryVulcan.Core.Storage;
using Xunit;

namespace HistoryJanus.Contracts;

public sealed class ProjectLifecycleContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HistoryJanus-Lifecycle", Guid.NewGuid().ToString("N"));
    private readonly string _data = Path.Combine(Path.GetTempPath(), "HistoryJanus-LifecycleData", Guid.NewGuid().ToString("N"));

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
