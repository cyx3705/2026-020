using System.IO;
using HistoryJanus.Git;
using HistoryVulcan.Core.Storage;
using Xunit;

namespace HistoryJanus.Contracts;

/// <summary>
/// 「同步」要把远端分支落成本地分支（5.10.0，REQ-022）。
///
/// 刷新只取跟踪引用：历史在本地、图上看得见，但 <c>git branch</c> 里没有它们。
/// 因此 main 一致并不等于同步完了——此时说「本地与远端已验证一致」并让它去归档，
/// 说的不是实话。全程用本机裸仓当远端，不碰外网。
/// </summary>
public sealed class RemoteBranchSyncContractTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "HistoryJanus-BranchSync", Guid.NewGuid().ToString("N"));

    private readonly string _data = Path.Combine(
        Path.GetTempPath(), "HistoryJanus-BranchSyncData", Guid.NewGuid().ToString("N"));

    private const string ProjectName = "2026-905-BranchSync";

    /// <summary>
    /// main 已经一致、远端还有本地没建的分支时，状态是「同步」而不是「归档」；
    /// 同步之后本地真的多出那条分支，且上游指向 origin；再刷新才回到可归档。
    /// </summary>
    [Fact]
    public async Task AProjectWithUnbuiltRemoteBranchesIsNotReportedAsReadyToArchive()
    {
        var (projects, project) = await SetUpAsync();
        await PushBranchFromElsewhereAsync("feature/someone-else");

        var before = await projects.RefreshProjectAsync(ProjectName);
        Assert.Equal(ProjectLifecycleState.Behind, before.State);
        Assert.Equal("同步", before.Action);
        Assert.Contains("1 条分支本地尚未建立", before.Message);

        var sync = await projects.SyncAsync(ProjectName);
        Assert.True(sync.Success, sync.Message);
        Assert.Contains("新建 1 条", sync.Message);

        var branches = await GitRunner.RunAsync(project,
            ["for-each-ref", "--format=%(refname)", "refs/heads"]);
        Assert.Contains("refs/heads/feature/someone-else", branches.Output);
        var upstream = await GitRunner.RunAsync(project,
            ["rev-parse", "--abbrev-ref", "feature/someone-else@{upstream}"]);
        Assert.True(upstream.Success, upstream.Output);
        Assert.Equal("origin/feature/someone-else", upstream.Output.Trim());

        var after = await projects.RefreshProjectAsync(ProjectName);
        Assert.Equal(ProjectLifecycleState.Synchronized, after.State);
        Assert.Equal("归档", after.Action);
    }

    /// <summary>
    /// 本地分支落后于远端且没被签出时快进；**领先或已分叉的一律不碰**。
    /// 自动化替人决定怎么并，是这条链路最不该做的事。
    /// </summary>
    [Fact]
    public async Task BehindBranchesFastForwardWhileDivergedOnesAreLeftAlone()
    {
        var (projects, project) = await SetUpAsync();
        await PushBranchFromElsewhereAsync("feature/behind");
        await PushBranchFromElsewhereAsync("feature/diverged");
        await projects.RefreshProjectAsync(ProjectName);

        // 两条都先落到本地，再让它们各自走偏。
        var first = await projects.SyncAsync(ProjectName);
        Assert.True(first.Success, first.Message);

        // behind：远端又前进一步，本地不动。
        await PushMoreFromElsewhereAsync("feature/behind");
        // diverged：本地分支自己走一步，远端不动。
        // 用 commit-tree 直接造提交、再 update-ref 挪分支——不碰 HEAD：
        // 在 main 上补一次提交会把项目整体变成「领先」，那时同步本来就会被拒，
        // 测到的就不是「分叉分支不碰」这件事了。
        var divergedBefore = await CommitOffToTheSideAsync(project, "feature/diverged");

        await projects.RefreshProjectAsync(ProjectName);
        var second = await projects.SyncAsync(ProjectName);

        var behindSha = await GitRunner.RunAsync(project, ["rev-parse", "feature/behind"]);
        var remoteBehind = await GitRunner.RunAsync(project,
            ["rev-parse", "refs/remotes/origin/feature/behind"]);
        Assert.Equal(remoteBehind.Output.Trim(), behindSha.Output.Trim());

        var divergedAfter = await GitRunner.RunAsync(project, ["rev-parse", "feature/diverged"]);
        Assert.Equal(divergedBefore, divergedAfter.Output.Trim());
        Assert.Contains("跳过", second.Message);
        Assert.Contains("本地领先或已分叉", second.Message);
    }

    /// <summary>
    /// 远端只有 main 时，同步的回执里不该多出一句关于分支的话——
    /// 「远端分支：」后面跟着空内容，比不说更让人以为出了什么事。
    /// </summary>
    [Fact]
    public async Task ASingleBranchRemoteSyncsWithoutMentioningBranches()
    {
        var (projects, _) = await SetUpAsync();
        await projects.RefreshProjectAsync(ProjectName);
        var sync = await projects.SyncAsync(ProjectName);
        Assert.True(sync.Success, sync.Message);
        Assert.Equal("同步完成，本地与远端已验证一致", sync.Message);
        Assert.Equal(ProjectLifecycleState.Synchronized, sync.Snapshot!.State);
    }

    /// <summary>
    /// <c>origin/HEAD</c> 不是一条可落地的分支，照它建本地分支只会多出一条名叫 HEAD 的重影。
    /// </summary>
    [Fact]
    public async Task TheRemoteHeadSymrefIsNotMaterialisedAsABranch()
    {
        var (projects, project) = await SetUpAsync();
        await Git(project, "remote", "set-head", "origin", "main");
        var gap = await RemoteBranchSync.ReadGapAsync(project, CancellationToken.None);
        Assert.Equal(0, gap.PendingCount);
    }

    /// <summary>
    /// 在一条分支上造一个只存在于本地的提交，**不动 HEAD、不动 main**。
    /// 返回分支的新 SHA。
    /// </summary>
    private static async Task<string> CommitOffToTheSideAsync(string project, string branch)
    {
        var tree = await Capture(project, ["rev-parse", "HEAD^{tree}"]);
        var parent = await Capture(project, ["rev-parse", branch]);
        var commit = await Capture(project, ["commit-tree", tree, "-p", parent, "-m", "local only"]);
        await Git(project, "update-ref", $"refs/heads/{branch}", commit, parent);
        return commit;
    }

    private static async Task<string> Capture(string directory, string[] args)
    {
        var result = await GitRunner.RunAsync(directory, args);
        Assert.True(result.Success, $"git {string.Join(' ', args)} failed: {result.Output}");
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
    }

    private string Remote => Path.Combine(_root, "remote.git");

    private async Task<(ProjectService Projects, string Project)> SetUpAsync()
    {
        Directory.CreateDirectory(_root);
        var project = Path.Combine(_root, ProjectName);
        await Git(_root, "init", "--bare", Remote);
        Directory.CreateDirectory(project);
        await Git(project, "init", "-b", "main");
        await Identity(project);
        await Git(project, "commit", "--allow-empty", "-m", "base");
        await Git(project, "remote", "add", "origin", Remote);
        await Git(project, "push", "-u", "origin", "main");

        var settings = new MemorySettings();
        settings.Set(ProjectService.KeyLibraryRoot, _root);
        return (new ProjectService(settings, _ => true, _data), project);
    }

    /// <summary>「别人的机器」：另一份克隆推一条分支上去。</summary>
    private async Task PushBranchFromElsewhereAsync(string branch)
    {
        var other = await OtherCloneAsync();
        await Git(other, "checkout", "-b", branch, "origin/main");
        await Git(other, "commit", "--allow-empty", "-m", $"work on {branch}");
        await Git(other, "push", "-u", "origin", branch);
    }

    private async Task PushMoreFromElsewhereAsync(string branch)
    {
        var other = await OtherCloneAsync();
        await Git(other, "checkout", "-B", branch, $"origin/{branch}");
        await Git(other, "commit", "--allow-empty", "-m", $"more on {branch}");
        await Git(other, "push", "origin", branch);
    }

    private async Task<string> OtherCloneAsync()
    {
        var other = Path.Combine(_root, "other");
        if (!Directory.Exists(other))
        {
            await Git(_root, "clone", Remote, other);
            await Identity(other);
        }
        else
        {
            await Git(other, "fetch", "origin");
        }
        return other;
    }

    /// <summary>两条 config 必须**依次**写：git 写 .git/config 要先拿文件锁，并发写会撞锁。</summary>
    private static async Task Identity(string directory)
    {
        await Git(directory, "config", "user.name", "Janus Test");
        await Git(directory, "config", "user.email", "janus@example.invalid");
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
}
