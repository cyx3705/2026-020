using System.IO;
using HistoryJanus.Git;
using HistoryVulcan.Core.Commands;
using Xunit;

namespace HistoryJanus.Contracts;

/// <summary>
/// REQ-JANUS-DIVERGE-001…004（5.15.0）：推送被拒与同步遇到分叉时，git 判文本冲突、AI 判语义、Janus 执行；
/// 真冲突、AI 否决、AI 不可用、未接 AI、工作树不干净，一律什么都不动。
/// </summary>
public sealed class DivergenceReconcileContractTests : IDisposable
{
    private const string Name = "2026-904-Diverge";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HistoryJanus-Diverge", Guid.NewGuid().ToString("N"));
    private readonly string _data = Path.Combine(Path.GetTempPath(), "HistoryJanus-DivergeData", Guid.NewGuid().ToString("N"));
    private string Project => Path.Combine(_root, Name);
    private string Peer => Path.Combine(_root, "peer");
    private string Remote => Path.Combine(_root, "remote.git");

    [Fact]
    public async Task SyncRebasesDisjointDivergenceAfterAiApprovalAndPushes()
    {
        await ArrangeDivergedAsync("local.txt", "local", "site.txt", "bot");
        var advisor = new FakeAdvisor(new DivergenceAdvice(true, "目录不交叠"));
        var service = CreateService(advisor);

        var sync = await service.SyncAsync(Name);

        Assert.True(sync.Success, sync.Message);
        Assert.Equal(ProjectLifecycleState.Synchronized, sync.Snapshot!.State);
        Assert.Contains("分叉已自动收口", sync.Message);
        Assert.Contains("目录不交叠", sync.Message);
        var facts = Assert.Single(advisor.Calls);
        Assert.Equal(DivergenceReconciler.RebaseStrategy, facts.Strategy);
        Assert.Empty(facts.OverlapFiles);
        Assert.Equal(["local.txt"], facts.LocalFiles);
        Assert.Equal(["site.txt"], facts.RemoteFiles);
        // 变基：历史是一条线，没有合并提交；远端已经有本地提交。
        Assert.Equal("0", await GitOut(Project, "rev-list", "--count", "--merges", "HEAD"));
        Assert.Equal(await GitOut(Project, "rev-parse", "HEAD"),
            await GitOut(Remote, "rev-parse", "refs/heads/main"));
    }

    [Fact]
    public async Task TextConflictReturnsFilesWithoutAskingAiOrTouchingHistory()
    {
        await ArrangeDivergedAsync("shared.txt", "local", "shared.txt", "remote");
        var advisor = new FakeAdvisor(new DivergenceAdvice(true, "不该被问到"));
        var service = CreateService(advisor);
        var before = await GitOut(Project, "rev-parse", "HEAD");

        var sync = await service.SyncAsync(Name);

        Assert.False(sync.Success);
        Assert.Contains("真冲突", sync.Message);
        Assert.Contains("shared.txt", sync.Message);
        Assert.Empty(advisor.Calls);
        Assert.Equal(before, await GitOut(Project, "rev-parse", "HEAD"));
        Assert.Equal(string.Empty, await GitOut(Project, "status", "--porcelain"));
    }

    [Theory]
    [InlineData("veto")]
    [InlineData("failure")]
    [InlineData("none")]
    public async Task VetoUnavailableOrMissingAiLeavesBothSidesUntouched(string mode)
    {
        await ArrangeDivergedAsync("local.txt", "local", "site.txt", "bot");
        var advisor = mode switch
        {
            "veto" => new FakeAdvisor(new DivergenceAdvice(false, "两边改了同一版本号")),
            "failure" => new FakeAdvisor(new DivergenceAdvice(false, string.Empty, "未配置密钥")),
            _ => null,
        };
        var service = CreateService(advisor);
        var local = await GitOut(Project, "rev-parse", "HEAD");
        var remote = await GitOut(Remote, "rev-parse", "refs/heads/main");

        var sync = await service.SyncAsync(Name);

        Assert.False(sync.Success);
        Assert.Contains(mode switch
        {
            "veto" => "两边改了同一版本号",
            "failure" => "AI 检查不可用",
            _ => "未接入 AI 检查",
        }, sync.Message);
        Assert.Equal(local, await GitOut(Project, "rev-parse", "HEAD"));
        Assert.Equal(remote, await GitOut(Remote, "rev-parse", "refs/heads/main"));
    }

    [Fact]
    public async Task RejectedPushReconcilesThenPushesAgain()
    {
        await ArrangeDivergedAsync("local.txt", "local", "site.txt", "bot", fetch: false);
        var advisor = new FakeAdvisor(new DivergenceAdvice(true, "互不相干"));
        var service = CreateService(advisor);

        var push = await service.PushAsync(Name);

        Assert.True(push.Success, push.Message);
        Assert.Contains("分叉已自动收口", push.Message);
        Assert.Single(advisor.Calls);
        Assert.Equal(await GitOut(Project, "rev-parse", "HEAD"),
            await GitOut(Remote, "rev-parse", "refs/heads/main"));
    }

    [Fact]
    public async Task DirtyWorktreeIsRefusedBeforeAnyCheck()
    {
        await ArrangeDivergedAsync("local.txt", "local", "site.txt", "bot", fetch: false);
        File.WriteAllText(Path.Combine(Project, "draft.txt"), "wip");
        var advisor = new FakeAdvisor(new DivergenceAdvice(true, "不该被问到"));
        await Git(Project, "fetch", "origin");

        var result = await DivergenceReconciler.ReconcileAsync(Project, "main", advisor, null, CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Refused, result.Outcome);
        Assert.Empty(advisor.Calls);
    }

    [Fact]
    public async Task PublishedLocalCommitsAreMergedNotRebased()
    {
        await ArrangeDivergedAsync("local.txt", "local", "site.txt", "bot");
        // 本地提交已经推到了别的远端分支：变基会改写别人可能已拿到的提交，必须用 merge。
        await Git(Project, "push", "origin", "HEAD:refs/heads/feature");
        await Git(Project, "fetch", "origin");
        var advisor = new FakeAdvisor(new DivergenceAdvice(true, "互不相干"));

        var result = await DivergenceReconciler.ReconcileAsync(Project, "main", advisor, null, CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Reconciled, result.Outcome);
        Assert.Equal(DivergenceReconciler.MergeStrategy, result.Strategy);
        Assert.Equal("1", await GitOut(Project, "rev-list", "--count", "--merges", "origin/main..HEAD"));
    }

    [Fact]
    public void ApolloAnswerIsReadStrictly()
    {
        var safe = ApolloDivergenceAdvisor.Read(CommandResult.Ok("{\"safe\": true, \"reason\": \"目录不交叠\"}"));
        Assert.True(safe.Proceed);
        Assert.Null(safe.Failure);
        Assert.Equal("目录不交叠", safe.Reason);

        Assert.False(ApolloDivergenceAdvisor.Read(CommandResult.Ok("{\"safe\": false, \"reason\": \"x\"}")).Proceed);
        Assert.NotNull(ApolloDivergenceAdvisor.Read(CommandResult.Ok("可以合并")).Failure);
        Assert.NotNull(ApolloDivergenceAdvisor.Read(CommandResult.Ok("{\"safe\": \"yes\"}")).Failure);
        Assert.NotNull(ApolloDivergenceAdvisor.Read(CommandResult.Fail("未知指令 apollo.chat.send")).Failure);
    }

    [Theory]
    [InlineData(" ! [rejected]        main -> main (fetch first)", true)]
    [InlineData(" ! [rejected]        main -> main (non-fast-forward)", true)]
    [InlineData(" ! [rejected]        main -> main (先获取)", true)]
    [InlineData(" ! [remote rejected] main -> main (pre-receive hook declined)", false)]
    [InlineData("fatal: early EOF", false)]
    public void OnlyNonFastForwardRejectionTriggersReconcile(string output, bool expected)
        => Assert.Equal(expected, ProjectService.IsNonFastForwardRejection(output));

    /// <summary>本地与远端各自在 initial 之后多一个提交；fetch=true 时本地已取回远端。</summary>
    private async Task ArrangeDivergedAsync(
        string localFile, string localText, string remoteFile, string remoteText, bool fetch = true)
    {
        Directory.CreateDirectory(_root);
        await Git(_root, "init", "--bare", "-b", "main", Remote);
        Directory.CreateDirectory(Project);
        await Git(Project, "init", "-b", "main");
        await Identity(Project, "Janus Test");
        File.WriteAllText(Path.Combine(Project, "shared.txt"), "base\n");
        await Git(Project, "add", "-A");
        await Git(Project, "commit", "-m", "initial");
        await Git(Project, "remote", "add", "origin", Remote);
        await Git(Project, "push", "-u", "origin", "main");

        await Git(_root, "clone", "--branch", "main", Remote, Peer);
        await Identity(Peer, "Janus Bot");
        File.WriteAllText(Path.Combine(Peer, remoteFile), remoteText + "\n");
        await Git(Peer, "add", "-A");
        await Git(Peer, "commit", "-m", "remote change");
        await Git(Peer, "push", "origin", "main");

        File.WriteAllText(Path.Combine(Project, localFile), localText + "\n");
        await Git(Project, "add", "-A");
        await Git(Project, "commit", "-m", "local change");
        if (fetch)
            await Git(Project, "fetch", "origin");
    }

    private ProjectService CreateService(IDivergenceAdvisor? advisor)
    {
        var settings = new MemorySettings();
        settings.Set(ProjectService.KeyLibraryRoot, _root);
        return new ProjectService(settings, _ => true, _data) { DivergenceAdvisor = advisor };
    }

    private static async Task Identity(string directory, string name)
    {
        await Git(directory, "config", "user.name", name);
        await Git(directory, "config", "user.email", "janus@example.invalid");
    }

    private static async Task Git(string directory, params string[] args)
    {
        var result = await GitRunner.RunAsync(directory, args);
        Assert.True(result.Success, $"git {string.Join(' ', args)} failed: {result.Output}");
    }

    private static async Task<string> GitOut(string directory, params string[] args)
    {
        var result = await GitRunner.RunAsync(directory, args);
        Assert.True(result.Success, $"git {string.Join(' ', args)} failed: {result.Output}");
        return result.Output.Trim();
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

    private sealed class FakeAdvisor(DivergenceAdvice answer) : IDivergenceAdvisor
    {
        public List<DivergenceFacts> Calls { get; } = [];

        public Task<DivergenceAdvice> AdviseAsync(DivergenceFacts facts, CancellationToken cancellation)
        {
            Calls.Add(facts);
            return Task.FromResult(answer);
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
