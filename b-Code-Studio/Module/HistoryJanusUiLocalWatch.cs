using System.IO;
using HistoryJanus.Git;
using HistoryVulcan.Core.Commands;

namespace HistoryJanus.Module;

/// <summary>
/// 本地改动无感刷新（5.16.2，用户定）：「刷新」按钮只管远端（fetch）；
/// 工作树变脏、新建项目、提交、切分支这些**本地**变化，界面自己跟上，不用人去按。
///
/// 做法是监视项目库目录：哪个项目里有文件变了，停手片刻后只把**这一个项目**按本地引用重读一遍
/// （git status + 本地跟踪引用，不 fetch），读数真的变了才让总览表重取。本地读一个项目是几十毫秒的事，
/// 改动再密也只是同一个项目排一次。
///
/// .git 目录里只认 HEAD、refs/heads 与 packed-refs（提交、切分支、建删分支落在这里）；
/// index、objects、日志和锁文件一律不理——本地重读自己跑的 git status 会刷新 index，
/// 若也算改动就会自己触发自己，永远停不下来。
/// </summary>
internal static partial class HistoryJanusUiCommands
{
    private static readonly object LocalWatchLock = new();

    private static LocalProjectWatcher? _localWatcher;

    /// <summary>首次给界面取总览时开始监视；之后再取数什么都不做。</summary>
    private static void EnsureLocalWatch(ICommandBus bus, ProjectService projects, string source)
    {
        lock (LocalWatchLock)
        {
            if (_localWatcher != null || !Directory.Exists(projects.LibraryRoot))
                return;
            _localWatcher = new LocalProjectWatcher(
                projects.LibraryRoot,
                name => OnLocalProjectChangedAsync(bus, projects, name, source));
        }
    }

    /// <summary>模块卸载（热重载）时停掉监视：旧程序集的回调不能再往新总线上发东西。</summary>
    internal static void StopLocalWatch()
    {
        lock (LocalWatchLock)
        {
            _localWatcher?.Dispose();
            _localWatcher = null;
        }
    }

    private static async Task OnLocalProjectChangedAsync(
        ICommandBus bus, ProjectService projects, string name, string source)
    {
        // 表格还没取过数（缓存为空）就没有要对齐的东西；第一次取数本来就会读到现场。
        if (_projectCache == null)
            return;

        if (!ProjectRepoLayout.IsRegisteredProjectName(name))
            return;
        var path = Path.Combine(projects.LibraryRoot, name);
        var registered = ProjectRepoLayout.IsIndependentGitRepo(path);
        var before = _projectCache.FirstOrDefault(item => item.BranchName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (!registered && before == null)
            return;

        if (!registered)
        {
            // 项目目录被删或改名：从总览里拿掉。归档项目也没有 .git，但它们的 z 文件夹还在、行要留着。
            if (Directory.Exists(path) || before!.IsArchived)
                return;
            await ProjectCacheGate.WaitAsync();
            try
            {
                _projectCache = _projectCache.Where(item => !ReferenceEquals(item, before)).ToArray();
            }
            finally { ProjectCacheGate.Release(); }
        }
        else
        {
            await RefreshCachedProjectAsync(projects, name, null);
            var after = _projectCache?.FirstOrDefault(item => item.BranchName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (before != null && after != null && SameLocalReading(before, after))
                return;
        }

        // 安静执行：自动对齐不是人的操作，不进控制台。
        _ = await bus.InvokeAsync("aurora.ui.refreshdata node=projects", source);
        _ = await bus.InvokeAsync("aurora.ui.refreshdata node=history-rows", source);
    }

    /// <summary>总览表上看得见的那几样有没有变。ZFolders 是列表，记录自带的相等比较只比引用，不能直接用。</summary>
    internal static bool SameLocalReading(WorktreeInfo before, WorktreeInfo after)
        => before.IsClean == after.IsClean
           && before.WorktreeStatusMessage == after.WorktreeStatusMessage
           && before.HeadBranch == after.HeadBranch
           && before.LastCommitTime == after.LastCommitTime
           && before.LastCommitMessage == after.LastCommitMessage
           && before.LifecycleState == after.LifecycleState
           && before.LifecycleAction == after.LifecycleAction
           && before.LifecycleMessage == after.LifecycleMessage
           && before.ZFolderCount == after.ZFolderCount;
}
