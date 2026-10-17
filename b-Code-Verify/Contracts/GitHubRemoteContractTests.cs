using System.IO;
using HistoryJanus.Git;
using HistoryJanus.GitHub;
using HistoryJanus.Module;
using HistoryVulcan.Core.Commands;
using Xunit;

namespace HistoryJanus.Contracts;

/// <summary>REQ-018：使用真实临时 Git 仓验证指定地址、项目隔离、预览和取消；不联网。</summary>
public sealed class GitHubRemoteContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Janus-Remote", Guid.NewGuid().ToString("N"));
    private const string Name = "2026-041-VideoStudio";
    private const string Url = "https://github.com/Cloudsflee/video-studio";

    [Fact]
    public void OnlyTheRemoteAddressRowIsEditableAndItCarriesItsSourceProject()
    {
        var overview = new GitHubAccountOverview("git", "gcm", false,
            new("User", "user@example.com", "local"), new(Url, Url, GitRemoteTransport.Https, "github.com", "owner", "repo"),
            [], new("github.com", "", [], "unknown", ""), new("unknown", "https", []), DateTimeOffset.UtcNow);
        var rows = HistoryJanusUiCommands.UiGitHubProjection.Read(overview, Name);
        var editable = Assert.Single(rows, row => row.GetValueOrDefault("editable") == "true");
        Assert.Equal("远端地址", editable["item"]);
        Assert.Equal(Name, editable["name"]);
        Assert.Equal(Url, editable["value"]);
        Assert.DoesNotContain(rows.Where(row => !ReferenceEquals(row, editable)), row => row.ContainsKey("editable"));
    }

    [Fact]
    public async Task PreviewDoesNotCreateOriginAndSingleAddressUpdatesBothUrls()
    {
        var project = await Init(Name);
        var service = new GitHubConnectionService(() => project);
        var preview = await service.SetRemoteAsync(Url, null, false);
        Assert.False(preview.Applied);
        Assert.Equal(Url, preview.After.PushUrl);
        Assert.False((await GitRunner.RunAsync(project, ["remote", "get-url", "origin"])).Success);

        await service.SetRemoteAsync(Url, null, true);
        Assert.Equal(Url, await Git(project, "remote", "get-url", "origin"));
        Assert.Equal(Url, await Git(project, "remote", "get-url", "--push", "origin"));
        await service.SetRemoteAsync("https://github.com/other/fetch-only.git", Url, true);
        Assert.Equal(Url, await Git(project, "remote", "get-url", "--push", "origin"));
        await service.SetRemoteAsync("https://github.com/other/replacement.git", null, true);
        Assert.Equal("https://github.com/other/replacement.git", await Git(project, "remote", "get-url", "--push", "origin"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PageSaveUsesExplicitProjectAndRespectsConfirmation(bool approved)
    {
        var project = await Init(Name);
        var other = await Init("2026-042-Other");
        await Git(other, "remote", "add", "origin", "https://github.com/other/untouched");
        var settings = new Settings();
        settings.Set(ProjectService.KeyLibraryRoot, _root);
        var registry = new TestRegistrar();
        string? prompt = null;
        var bus = new TestCommandBus(registry, text => { prompt = text; return approved; });
        var business = StudioBusinessCompositionFactory.Register(registry, bus, settings, new TestLog(),
            Path.Combine(_root, "data"), () => "2026-042-Other");
        HistoryJanusUiCommands.Register(registry, bus, "test", () => business);
        bus.Fallback = (_, _) => Task.FromResult(CommandResult.Ok("refreshed"));
        var result = await bus.ExecuteAsync($"janus.ui.setremote name={Name} url={Url}", "test");
        Assert.Equal(approved, result.Success);
        Assert.Contains(Name, prompt);
        Assert.Contains(Url, prompt);
        Assert.Equal("https://github.com/other/untouched", await Git(other, "remote", "get-url", "origin"));
        var configured = await GitRunner.RunAsync(project, ["remote", "get-url", "origin"]);
        Assert.Equal(approved, configured.Success);
        Assert.Equal(approved, bus.Calls.Any(call => call.Text == "aurora.ui.refreshdata node=github-rows"));
        if (approved)
        {
            Assert.Equal(Url, configured.Output.Trim());
            Assert.Equal(Url, await Git(project, "remote", "get-url", "--push", "origin"));
        }
        Assert.False((await bus.ExecuteAsync($"janus.github.remote fetch={Url} apply=true", "test")).Success);
        Assert.False((await bus.ExecuteAsync("janus.github.status", "test")).Success);
        Assert.False((await bus.ExecuteAsync($"janus.github.remote name=../{Name} fetch={Url} apply=true", "test")).Success);
        Assert.False((await bus.ExecuteAsync($"janus.github.remote name=2026-999-Missing fetch={Url} apply=true", "test")).Success);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedPushUrlWriteRestoresTheOriginalRemote(bool existed)
    {
        var project = await Init(Name);
        if (existed)
        {
            await Git(project, "remote", "add", "origin", "https://github.com/old/fetch");
            await Git(project, "remote", "set-url", "--push", "origin", "https://github.com/old/push");
        }
        var service = new GitHubConnectionService(() => project, new FailingPushRunner());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetRemoteAsync(Url,
            "https://github.com/new/push", true));
        var remote = await GitRunner.RunAsync(project, ["remote", "get-url", "origin"]);
        Assert.Equal(existed, remote.Success);
        if (existed)
        {
            Assert.Equal("https://github.com/old/fetch", remote.Output.Trim());
            Assert.Equal("https://github.com/old/push", await Git(project, "remote", "get-url", "--push", "origin"));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("--upload-pack=bad")]
    [InlineData("https://name:secret@github.com/o/r")]
    [InlineData("https://github.com/o/r?token=bad")]
    public async Task InvalidAddressLeavesOriginAbsent(string url)
    {
        var project = await Init(Name);
        var service = new GitHubConnectionService(() => project);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SetRemoteAsync(url, null, true));
        Assert.False((await GitRunner.RunAsync(project, ["remote", "get-url", "origin"])).Success);
    }

    private async Task<string> Init(string name)
    {
        var project = Path.Combine(_root, name);
        Directory.CreateDirectory(project);
        await Git(project, "init", "-b", "main");
        return project;
    }

    private static async Task<string> Git(string path, params string[] args)
    {
        var result = await GitRunner.RunAsync(path, args);
        Assert.True(result.Success, result.Output);
        return result.Output.Trim();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) ProjectRepoLayout.DeleteTree(_root);
    }

    private sealed class FailingPushRunner : IToolProcessRunner
    {
        private readonly ToolProcessRunner _runner = new();
        public Task<ToolProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
            int timeoutSeconds = 30, CancellationToken cancellation = default)
            => arguments.Contains("set-url") && arguments.Contains("--push")
                ? Task.FromResult(new ToolProcessResult(1, "", "simulated push URL failure"))
                : _runner.RunAsync(fileName, arguments, timeoutSeconds, cancellation);
    }

    private sealed class Settings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public int GetInt(string key, int fallback) => int.TryParse(Get(key), out var value) ? value : fallback;
        public void Set(string key, string value) => _values[key] = value;
        public IReadOnlyList<KeyValuePair<string, string>> All() => _values.ToList();
    }
}

