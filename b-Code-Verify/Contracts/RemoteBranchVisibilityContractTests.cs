using System.IO;
using HistoryJanus.Git;
using HistoryVulcan.Core.Storage;
using Xunit;

namespace HistoryJanus.Contracts;

/// <summary>
/// 别人在远端开的分支要能进到本地、并且画得出来（5.10.0，REQ-019）。
///
/// 5.9 及以前有两道门各挡一半：刷新的 fetch 只取 main，分支根本到不了本地；
/// 图谱的泳道筛选只收 <c>ai/&lt;项目名&gt;/…</c>，到了本地也画不出来。
/// 两道门只修一道，现象都还是「同步过了却没有这段历史」，因此一起钉在这里。
/// 全程用本机裸仓当远端，不碰外网。
/// </summary>
public sealed class RemoteBranchVisibilityContractTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "HistoryJanus-RemoteBranch", Guid.NewGuid().ToString("N"));

    private readonly string _data = Path.Combine(
        Path.GetTempPath(), "HistoryJanus-RemoteBranchData", Guid.NewGuid().ToString("N"));

    private const string ProjectName = "2026-904-RemoteBranch";

    /// <summary>
    /// 刷新取的是 origin 的**全部**分支。命令行上给显式 refspec 会顶掉
    /// <c>remote.origin.fetch</c>，所以这条常量一旦退回成单分支写法，
    /// 别人推的分支就会再次悄悄消失——只测常量不够，下面那条测真的 fetch。
    /// </summary>
    [Fact]
    public void RefreshFetchesEveryRemoteHeadNotOnlyMainline()
    {
        Assert.Equal("+refs/heads/*:refs/remotes/origin/*", ProjectService.AllHeadsRefspec);
        Assert.DoesNotContain(ProjectService.MainlineBranch, ProjectService.AllHeadsRefspec);
    }

    /// <summary>
    /// 一次刷新之后，别人推上去的分支在本地有了跟踪引用，图谱也把它画成平行泳道，
    /// 并按与主线的 merge-base 给出基线。分支名故意取 <c>feature/…</c>：
    /// 它不带 <c>ai/</c> 前缀，正是 5.9 会丢掉的那一类。
    /// </summary>
    [Fact]
    public async Task ARefreshBringsSomeoneElsesBranchHomeAndTheGraphDrawsIt()
    {
        Directory.CreateDirectory(_root);
        var remote = Path.Combine(_root, "remote.git");
        var project = Path.Combine(_root, ProjectName);
        await Git(_root, "init", "--bare", remote);
        Directory.CreateDirectory(project);
        await Git(project, "init", "-b", "main");
        await Identity(project);
        await Git(project, "commit", "--allow-empty", "-m", "base");
        await Git(project, "remote", "add", "origin", remote);
        await Git(project, "push", "-u", "origin", "main");

        // 「别人的机器」：另一份克隆推一条分支上去，本地这一份对它一无所知。
        var other = Path.Combine(_root, "other");
        await Git(_root, "clone", remote, other);
        await Identity(other);
        await Git(other, "checkout", "-b", "feature/bridge-guard");
        await Git(other, "commit", "--allow-empty", "-m", "someone else's work");
        await Git(other, "push", "-u", "origin", "feature/bridge-guard");

        var projects = CreateService();
        var before = await GitRunner.RunAsync(project, ["for-each-ref", "--format=%(refname)", "refs/remotes"]);
        Assert.DoesNotContain("feature/bridge-guard", before.Output);

        var snapshot = await projects.RefreshProjectAsync(ProjectName);
        Assert.NotEqual(ProjectLifecycleState.Unavailable, snapshot.State);

        var after = await GitRunner.RunAsync(project, ["for-each-ref", "--format=%(refname)", "refs/remotes"]);
        Assert.Contains("refs/remotes/origin/feature/bridge-guard", after.Output);

        var graph = await new GraphService(projects).GetBranchesAsync(ProjectName, CancellationToken.None);
        Assert.True(graph.Success, graph.Message);
        var lane = Assert.Single(graph.Report!.Parallels);
        Assert.Equal("feature/bridge-guard", lane.Name);
        Assert.True(lane.IsOpen, "分支领先主线，泳道应是开着的");
        var relation = Assert.Single(
            graph.Report.Relations, item => item.BranchName == "feature/bridge-guard");
        Assert.Equal(ProjectName, relation.ParentBranch);
        Assert.NotEqual("", relation.BaselineSha);
    }

    /// <summary>
    /// <c>origin/HEAD</c> 是指向主线的符号引用，没有自己的历史。放宽泳道筛选以后
    /// 它会跟着 <c>refs/remotes</c> 一起被列出来，画出来就是主线的一份重影。
    /// </summary>
    [Fact]
    public async Task TheRemoteHeadSymrefDoesNotBecomeItsOwnLane()
    {
        Directory.CreateDirectory(_root);
        var remote = Path.Combine(_root, "remote.git");
        var project = Path.Combine(_root, ProjectName);
        await Git(_root, "init", "--bare", remote);
        Directory.CreateDirectory(project);
        await Git(project, "init", "-b", "main");
        await Identity(project);
        await Git(project, "commit", "--allow-empty", "-m", "base");
        await Git(project, "remote", "add", "origin", remote);
        await Git(project, "push", "-u", "origin", "main");
        await Git(project, "remote", "set-head", "origin", "main");

        var graph = await new GraphService(CreateService())
            .GetBranchesAsync(ProjectName, CancellationToken.None);
        Assert.True(graph.Success, graph.Message);
        Assert.Empty(graph.Report!.Parallels);
    }

    private ProjectService CreateService()
    {
        var settings = new MemorySettings();
        settings.Set(ProjectService.KeyLibraryRoot, _root);
        return new ProjectService(settings, _ => true, _data);
    }

    private static Task Identity(string directory)
        => Task.WhenAll(
            Git(directory, "config", "user.name", "Janus Test"),
            Git(directory, "config", "user.email", "janus@example.invalid"));

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
