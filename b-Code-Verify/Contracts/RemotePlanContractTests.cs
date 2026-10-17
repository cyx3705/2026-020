using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using HistoryJanus.Git;
using HistoryJanus.GitHub;
using HistoryJanus.Module;
using Xunit;

namespace HistoryJanus.Contracts;

/// <summary>REQ-019：新项目只保存可修改的建议地址，首次推送才复用或创建；已有 origin 始终优先。</summary>
public sealed class RemotePlanContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Janus-RemotePlan", Guid.NewGuid().ToString("N"));
    private const string Name = "2026-991-NewProject";

    [Fact]
    public void TablePrefersActualOriginAndOnlyFallsBackToThePendingAddress()
    {
        var overview = new GitHubAccountOverview("git", "gcm", false, new("", "", "local"),
            new("git@github.com:existing/project.git", "git@github.com:existing/push.git", GitRemoteTransport.Ssh, "github.com", "existing", "project"),
            [], new("github.com", "", [], "", ""), new("", "", []), DateTimeOffset.UtcNow,
            "https://github.com/suggested/project");
        var actual = HistoryJanusUiCommands.UiGitHubProjection.Read(overview, Name);
        Assert.Equal(overview.Origin.FetchUrl, Assert.Single(actual, row => row["item"] == "远端地址")["value"]);
        var pending = HistoryJanusUiCommands.UiGitHubProjection.Read(overview with
            { Origin = new("", "", GitRemoteTransport.None, "", "", "") }, Name);
        Assert.Equal(overview.PlannedRemoteUrl, Assert.Single(pending, row => row["item"] == "远端地址")["value"]);
        Assert.Contains("待首次推送", Assert.Single(pending, row => row["item"] == "远端状态")["value"]);
    }

    [Fact]
    public async Task FailedProvisionKeepsPendingAddressAndLeavesOriginAbsent()
    {
        var project = await Init(Name);
        const string url = "https://github.com/chosen-owner/custom";
        await Git(project, "config", ProjectService.KeyPlannedRemote, url);
        var service = Service();
        service.RepositoryProvisioner = new FailedProvisioner();
        var result = await service.PushAsync(Name);
        Assert.False(result.Success);
        Assert.Equal(url, await Git(project, "config", "--get", ProjectService.KeyPlannedRemote));
        Assert.False((await GitRunner.RunAsync(project, ["remote", "get-url", "origin"])).Success);
    }

    [Fact]
    public async Task CreatePrefillsFromTemplateWithoutOriginAndEditingStaysPending()
    {
        var template = await Init("0000-000-Template");
        await Git(template, "remote", "add", "origin", "git@github.com:template-owner/template.git");
        var service = Service();
        var created = await service.CreateAsync(Name, null, null);
        Assert.True(created.Success, created.Message);
        var project = Path.Combine(_root, Name);
        Assert.Equal("https://github.com/template-owner/2026-991", await Git(project, "config", "--local", "--get", ProjectService.KeyPlannedRemote));
        Assert.False((await GitRunner.RunAsync(project, ["remote", "get-url", "origin"])).Success);
        var connection = new GitHubConnectionService(() => project);
        var edited = await connection.SetRemoteAsync("https://github.com/other-owner/custom", null, true);
        Assert.True(edited.Applied);
        Assert.Equal("https://github.com/other-owner/custom", await Git(project, "config", "--local", "--get", ProjectService.KeyPlannedRemote));
        Assert.False((await GitRunner.RunAsync(project, ["remote", "get-url", "origin"])).Success);
        var state = await service.RefreshProjectAsync(Name, true);
        Assert.Equal("推送", state.Action);
        Assert.Equal("git@github.com:template-owner/template.git", await Git(template, "remote", "get-url", "origin"));
    }

    [Fact]
    public async Task FirstPushUsesExactPlannedAddressAndFollowingPushUsesExistingOrigin()
    {
        var project = await Init(Name);
        var remote = Path.Combine(_root, "remote.git");
        await Git(_root, "init", "--bare", "-b", "main", remote);
        const string url = "https://github.com/chosen-owner/custom-repository";
        await Git(project, "config", ProjectService.KeyPlannedRemote, url);
        // 本仓局部 URL 重写把传输送至临时裸仓；整个测试不访问网络。
        await Git(project, "config", $"url.{remote.Replace('\\', '/')}.insteadOf", url);
        var provisioner = new RecordingProvisioner(remote);
        var service = Service();
        service.RepositoryProvisioner = provisioner;
        var pushed = await service.PushAsync(Name);
        Assert.True(pushed.Success, pushed.Message);
        Assert.Equal("chosen-owner/custom-repository", Assert.Single(provisioner.Targets));
        Assert.Equal(url, await Git(project, "config", "--get", "remote.origin.url"));
        Assert.Equal(url, pushed.RemoteUrl);
        Assert.Equal(await Git(project, "rev-parse", "HEAD"), await Git(remote, "rev-parse", "refs/heads/main"));
        Assert.False((await GitRunner.RunAsync(project, ["config", "--get", ProjectService.KeyPlannedRemote])).Success);
        // 即使残留旧建议，已有 origin 也不再进入建仓路径。
        await Git(project, "config", ProjectService.KeyPlannedRemote, "https://github.com/wrong/project");
        var again = await service.PushAsync(Name);
        Assert.True(again.Success, again.Message);
        Assert.Single(provisioner.Targets);
        Assert.Equal(url, await Git(project, "config", "--get", "remote.origin.url"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProvisionerChecksTheSpecifiedOwnerBeforeCreating(bool exists)
    {
        var calls = new List<string>();
        using var handler = new Handler((method, path) =>
        {
            calls.Add(method + " " + path);
            if (path == "/repos/team/custom") return (exists ? HttpStatusCode.OK : HttpStatusCode.NotFound, Repo());
            if (path == "/user") return (HttpStatusCode.OK, "{\"login\":\"personal\"}");
            if (path == "/orgs/team/repos") return (HttpStatusCode.Created, Repo());
            return (HttpStatusCode.BadRequest, "{}");
        });
        using var provisioner = new GitHubRepositoryProvisioner(handler, (_, _) => Task.FromResult<string?>("test-token"));
        var result = await provisioner.EnsureAsync("unused", "team/custom", "public");
        Assert.Equal(!exists, result.Created);
        Assert.Equal("team/custom", result.FullName);
        Assert.Equal(exists ? new[] { "GET /repos/team/custom" }
            : new[] { "GET /repos/team/custom", "GET /user", "POST /orgs/team/repos" }, calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task UncertainRemoteStateNeverTriggersCreation(HttpStatusCode status)
    {
        var calls = 0;
        using var handler = new Handler((_, _) => { calls++; return (status, "{}"); });
        using var provisioner = new GitHubRepositoryProvisioner(handler, (_, _) => Task.FromResult<string?>("test-token"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provisioner.EnsureAsync("unused", "team/custom", "public"));
        Assert.Equal(1, calls);
    }

    private static string Repo() => """{"name":"custom","full_name":"team/custom","clone_url":"https://github.com/team/custom.git","ssh_url":"git@github.com:team/custom.git","visibility":"public"}""";

    private ProjectService Service()
    {
        var settings = new Settings();
        settings.Set(ProjectService.KeyLibraryRoot, _root);
        return new ProjectService(settings, _ => true, Path.Combine(_root, "data"));
    }

    private async Task<string> Init(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        await Git(path, "init", "-b", "main");
        await Git(path, "config", "user.name", "Test");
        await Git(path, "config", "user.email", "test@example.invalid");
        await Git(path, "commit", "--allow-empty", "-m", "seed");
        return path;
    }

    private static async Task<string> Git(string path, params string[] args)
    {
        var result = await GitRunner.RunAsync(path, args);
        Assert.True(result.Success, result.Output);
        return result.Output.Trim();
    }

    public void Dispose() { if (Directory.Exists(_root)) ProjectRepoLayout.DeleteTree(_root); }

    private sealed class Settings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new();
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public int GetInt(string key, int fallback) => fallback;
        public void Set(string key, string value) => _values[key] = value;
        public IReadOnlyList<KeyValuePair<string, string>> All() => _values.ToList();
    }

    private sealed class RecordingProvisioner(string remote) : IGitHubRepositoryProvisioner
    {
        public List<string> Targets { get; } = [];
        public Task<GitHubRepositoryCreation> EnsureAsync(string repositoryPath, string repositoryName, string visibility, CancellationToken cancellation = default)
        {
            Targets.Add(repositoryName);
            return Task.FromResult(new GitHubRepositoryCreation("custom-repository", repositoryName, remote, remote, "", false, visibility));
        }
    }

    private sealed class FailedProvisioner : IGitHubRepositoryProvisioner
    {
        public Task<GitHubRepositoryCreation> EnsureAsync(string repositoryPath, string repositoryName, string visibility, CancellationToken cancellation = default)
            => throw new InvalidOperationException("unavailable");
    }

    private sealed class Handler(Func<string, string, (HttpStatusCode Status, string Body)> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = respond(request.Method.Method, request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(response.Status) { Content = new StringContent(response.Body, Encoding.UTF8, "application/json") });
        }
    }
}
