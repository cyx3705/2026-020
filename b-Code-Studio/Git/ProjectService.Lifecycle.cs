using System.IO;

namespace HistoryJanus.Git;

public enum ProjectLifecycleState
{
    Dirty,
    Ahead,
    NoRemote,
    Unverified,
    Behind,
    Diverged,
    Synchronized,
    Archived,
    Unavailable,
}

public sealed record ProjectLifecycleSnapshot(
    string ProjectName,
    ProjectLifecycleState State,
    string Action,
    string Message,
    string LocalSha,
    string RemoteSha,
    string Remote,
    string Branch,
    int Ahead,
    int Behind,
    IReadOnlyList<string> ZFolders);

public sealed partial class ProjectService
{
    /// <summary>远端状态没能确认时的动作：重新查一次这个项目。</summary>
    public const string RefreshAction = "刷新";

    public IReadOnlyList<ProjectLifecycleRecord> LifecycleRecords => _lifecycle.All();

    public ProjectLifecycleRecord? GetLifecycleRecord(string name) => _lifecycle.Get(name);

    public async Task<ProjectLifecycleSnapshot> RefreshProjectAsync(
        string name, bool fetchRemote = true, CancellationToken cancellation = default)
    {
        name = name.Trim();
        var record = _lifecycle.Get(name);
        var path = Path.Combine(LibraryRoot, name);
        // 归档记录在、原地又不是仓，就是归档态——不要求目录还在：归档后只剩 z/Z 的项目
        // 一个 z 都没有时目录可能被手工清掉，那时它仍然是「已归档」，不是「读不了仓库」。
        if (record?.ArchivedAt != null && !ProjectRepoLayout.IsIndependentGitRepo(path))
        {
            return new ProjectLifecycleSnapshot(name, ProjectLifecycleState.Archived, "拉取",
                "项目已归档，本地只保留 z/Z 文件夹", record.VerifiedSha, record.VerifiedSha,
                record.Remote, record.Branch, 0, 0, ReadZFolderNames(path));
        }

        var resolved = ResolveWorktree(name);
        if (!resolved.Success || resolved.Worktree == null)
        {
            return new ProjectLifecycleSnapshot(name, ProjectLifecycleState.Unavailable, RefreshAction,
                resolved.Message, "", "", record?.Remote ?? "", record?.Branch ?? MainlineBranch,
                0, 0, Directory.Exists(path) ? ReadZFolderNames(path) : []);
        }

        var repo = resolved.Worktree.WorktreePath;
        var zFolders = ReadZFolderNames(repo);
        var status = await GitRunner.RunAsync(repo,
            ["status", "--porcelain=v1", "--untracked-files=all"], cancellation);
        var local = await ReadRefAsync(repo, "HEAD", cancellation);
        var branchResult = await GitRunner.RunAsync(repo, ["rev-parse", "--abbrev-ref", "HEAD"], cancellation);
        var branch = branchResult.Success ? branchResult.Output.Trim() : MainlineBranch;
        var remoteResult = await GitRunner.RunAsync(repo, ["remote", "get-url", "origin"], cancellation);
        var remote = remoteResult.Success ? remoteResult.Output.Trim() : "";

        if (!status.Success || local.Length == 0)
            return Snapshot(ProjectLifecycleState.Unavailable, RefreshAction, "无法读取本地仓库状态");
        if (!string.IsNullOrWhiteSpace(status.Output))
            return Snapshot(ProjectLifecycleState.Dirty, "提交", "工作树有未提交或未跟踪内容");
        if (remote.Length == 0)
            return Snapshot(ProjectLifecycleState.NoRemote, "推送", "项目尚未配置 origin");

        if (fetchRemote)
        {
            var fetch = await GitRunner.RunAsync(repo,
                ["fetch", "--no-tags", "origin", $"+refs/heads/{MainlineBranch}:refs/remotes/origin/{MainlineBranch}"],
                cancellation);
            if (!fetch.Success)
                return Snapshot(ProjectLifecycleState.Unavailable, RefreshAction, $"远端状态待确认: {fetch.Output}");
        }

        var remoteSha = await ReadRefAsync(repo, $"refs/remotes/origin/{MainlineBranch}", cancellation);
        if (remoteSha.Length == 0)
            return Snapshot(ProjectLifecycleState.NoRemote, "推送", "origin 尚无 main 分支");
        var counts = await GitRunner.RunAsync(repo,
            ["rev-list", "--left-right", "--count", $"{local}...{remoteSha}"], cancellation);
        if (!counts.Success)
            return Snapshot(ProjectLifecycleState.Unavailable, RefreshAction, "无法比较本地与远端提交");
        var parts = counts.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var ahead = 0;
        var behind = 0;
        if (parts.Length > 0) _ = int.TryParse(parts[0], out ahead);
        if (parts.Length > 1) _ = int.TryParse(parts[1], out behind);
        if (ahead > 0 && behind == 0)
            return Snapshot(ProjectLifecycleState.Ahead, "推送", $"本地领先 {ahead} 个提交", remoteSha, ahead, behind);
        if (ahead == 0 && behind > 0)
            return Snapshot(ProjectLifecycleState.Behind, "同步", $"远端领先 {behind} 个提交", remoteSha, ahead, behind);
        if (ahead > 0 && behind > 0)
            return Snapshot(ProjectLifecycleState.Diverged, "同步", $"分支已分叉：本地 {ahead} / 远端 {behind}", remoteSha, ahead, behind);
        if (record == null || !record.VerifiedSha.Equals(local, StringComparison.OrdinalIgnoreCase))
            return Snapshot(ProjectLifecycleState.Unverified, "同步", "两端提交一致，尚未完成同步校验", remoteSha);
        return Snapshot(ProjectLifecycleState.Synchronized, "归档", "本地与远端已验证一致", remoteSha);

        ProjectLifecycleSnapshot Snapshot(
            ProjectLifecycleState state, string action, string message,
            string remoteSha = "", int ahead = 0, int behind = 0)
            => new(name, state, action, message, local, remoteSha, remote, branch,
                ahead, behind, zFolders);
    }

    public async Task<IReadOnlyList<ProjectLifecycleSnapshot>> RefreshProjectsAsync(
        CancellationToken cancellation = default)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(LibraryRoot))
        {
            foreach (var path in Directory.EnumerateDirectories(LibraryRoot))
            {
                var name = Path.GetFileName(path);
                if (ProjectRepoLayout.IsRegisteredProjectName(name)
                    && (ProjectRepoLayout.IsIndependentGitRepo(path) || _lifecycle.Get(name)?.ArchivedAt != null))
                    names.Add(name);
            }
        }
        foreach (var record in _lifecycle.All().Where(item => item.ArchivedAt != null))
            names.Add(record.ProjectName);
        return await RefreshManyAsync(
            names.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList(),
            fetchRemote: true, cancellation);
    }

    public async Task<List<WorktreeInfo>> ReadLifecycleStatusesAsync(
        IReadOnlyList<WorktreeInfo> worktrees, bool fetchRemote, CancellationToken cancellation = default)
    {
        var snapshots = await RefreshManyAsync(
            worktrees.Select(item => item.BranchName).ToList(), fetchRemote, cancellation);
        var byName = snapshots.ToDictionary(item => item.ProjectName, StringComparer.OrdinalIgnoreCase);
        return worktrees.Select(item => ApplyLifecycle(item, byName[item.BranchName])).ToList();
    }

    /// <summary>
    /// 同时在跑的 fetch 上限。全库有四十多个项目，此前一次刷新会同时开四十多条 ssh
    /// 传输；本机链路撑不住那个并发，结果是每一条都变慢、偶发中断，而同一时刻若还有
    /// 一次归档拉取在跑，它会被挤到近乎不动。限流之后总时长并没有变长——
    /// 链路带宽就那么多，排队只是把它按顺序用完。
    /// </summary>
    private const int MaxConcurrentFetches = 6;

    private static async Task<IReadOnlyList<ProjectLifecycleSnapshot>> RefreshManyAsync(
        IReadOnlyList<string> names,
        Func<string, Task<ProjectLifecycleSnapshot>> refresh)
    {
        var results = new ProjectLifecycleSnapshot[names.Count];
        using var gate = new SemaphoreSlim(MaxConcurrentFetches, MaxConcurrentFetches);
        await Task.WhenAll(names.Select(async (name, index) =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                results[index] = await refresh(name).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }));
        return results;
    }

    private Task<IReadOnlyList<ProjectLifecycleSnapshot>> RefreshManyAsync(
        IReadOnlyList<string> names, bool fetchRemote, CancellationToken cancellation)
        => RefreshManyAsync(names, name => RefreshProjectAsync(name, fetchRemote, cancellation));

    /// <summary>
    /// 把一份已经取到的状态快照写进项目行。调用方手上已有 fetch 过的快照时用它，
    /// 不要再按 fetchRemote=false 重算——断网时那样会用陈旧的远端引用把「刷新」算回别的动作。
    /// </summary>
    public static WorktreeInfo ApplyLifecycle(WorktreeInfo item, ProjectLifecycleSnapshot state)
        => item with
        {
            IsArchived = state.State == ProjectLifecycleState.Archived,
            IsClean = state.State == ProjectLifecycleState.Dirty ? false
                : state.State == ProjectLifecycleState.Unavailable ? null : true,
            WorktreeStatusMessage = state.Message,
            ZFolderCount = state.ZFolders.Count,
            ZFolders = state.ZFolders,
            LifecycleState = state.State.ToString(),
            LifecycleAction = state.Action,
            LifecycleMessage = state.Message,
        };

    public void InvalidateVerification(string name)
    {
        var record = _lifecycle.Get(name.Trim());
        if (record != null)
            _lifecycle.Save(record with { VerifiedSha = "", ArchivedAt = null });
    }

    public async Task<(bool Success, string Message, ProjectLifecycleSnapshot? Snapshot)> SyncAsync(
        string name, CancellationToken cancellation = default)
    {
        var before = await RefreshProjectAsync(name, fetchRemote: true, cancellation);
        if (before.State == ProjectLifecycleState.Diverged)
            return (false, "本地与远端已分叉，拒绝自动 merge/rebase；请人工处理后重试同步", before);
        if (before.State is ProjectLifecycleState.Dirty or ProjectLifecycleState.NoRemote
            or ProjectLifecycleState.Ahead or ProjectLifecycleState.Archived)
            return (false, $"当前状态应执行“{before.Action}”，不能同步：{before.Message}", before);
        if (before.State == ProjectLifecycleState.Unavailable)
            return (false, before.Message, before);

        var repo = Path.Combine(LibraryRoot, name.Trim());
        if (before.State == ProjectLifecycleState.Behind)
        {
            var merge = await GitRunner.RunAsync(repo,
                ["merge", "--ff-only", $"refs/remotes/origin/{MainlineBranch}"], cancellation);
            if (!merge.Success)
                return (false, $"仅快进同步失败，未执行 merge/rebase:\n{merge.Output}", before);
        }

        var local = await ReadRefAsync(repo, "HEAD", cancellation);
        var remoteSha = await ReadRefAsync(repo, $"refs/remotes/origin/{MainlineBranch}", cancellation);
        if (local.Length == 0 || !local.Equals(remoteSha, StringComparison.OrdinalIgnoreCase))
            return (false, "同步后本地与远端 SHA 仍不一致", before);
        _lifecycle.Save(new ProjectLifecycleRecord(name.Trim(), before.Remote, before.Branch,
            local, ReadZFolderNames(repo), null));
        var after = await RefreshProjectAsync(name, fetchRemote: false, cancellation);
        return (true, "同步完成，本地与远端已验证一致", after);
    }

    public async Task<(bool Success, string Message)> ArchiveAsync(
        string name, CancellationToken cancellation = default)
    {
        name = name.Trim();
        if (IsProtected(name))
            return (false, $"\"{name}\" 是受保护项目，拒绝归档");
        var snapshot = await RefreshProjectAsync(name, fetchRemote: true, cancellation);
        if (snapshot.State != ProjectLifecycleState.Synchronized)
            return (false, $"归档前复核未通过，应执行“{snapshot.Action}”：{snapshot.Message}");

        var source = Path.Combine(LibraryRoot, name);
        if (!TryValidateManagedDirectChild(source, true, out var pathError))
            return (false, $"项目路径不安全: {pathError}");
        var stage = Path.Combine(LibraryRoot, $".janus-archive-{Guid.NewGuid():N}");
        var backup = Path.Combine(LibraryRoot, $".janus-backup-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(stage);
            foreach (var z in snapshot.ZFolders)
                ProjectRepoLayout.CopyDirectory(Path.Combine(source, z), Path.Combine(stage, z));
            Directory.Move(source, backup);
            try
            {
                Directory.Move(stage, source);
            }
            catch
            {
                Directory.Move(backup, source);
                throw;
            }
            try
            {
                _lifecycle.Save(new ProjectLifecycleRecord(name, snapshot.Remote, snapshot.Branch,
                    snapshot.LocalSha, snapshot.ZFolders, DateTimeOffset.Now));
            }
            catch
            {
                Directory.Move(source, stage);
                Directory.Move(backup, source);
                throw;
            }
            _tree.InvalidateCache();
            try
            {
                ProjectRepoLayout.DeleteTree(backup);
                return (true, $"项目已归档，仅保留 {snapshot.ZFolders.Count} 个 z/Z 文件夹");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (true, $"项目已归档；旧工作树备份清理失败: {ex.Message}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (Directory.Exists(stage)) ProjectRepoLayout.DeleteTree(stage);
            return (false, $"归档失败，原工作树已保留或回滚: {ex.Message}");
        }
    }

    public async Task<(bool Success, string Message)> PullAsync(
        string name,
        IProgress<string>? progress = null,
        CancellationToken cancellation = default)
    {
        name = name.Trim();
        var record = _lifecycle.Get(name);
        if (record?.ArchivedAt == null)
            return (false, "项目没有归档记录");
        if (string.IsNullOrWhiteSpace(record.Remote))
            return (false, $"归档记录里没有 origin 地址，无法拉取: {name}");
        var target = Path.Combine(LibraryRoot, name);
        if (!TryValidateManagedDirectChild(target, true, out var pathError))
            return (false, $"项目路径不安全: {pathError}");
        var stage = Path.Combine(LibraryRoot, $".janus-pull-{Guid.NewGuid():N}");
        var backup = Path.Combine(LibraryRoot, $".janus-backup-{Guid.NewGuid():N}");
        // 分段取：整包克隆在本机链路上会断（见 ArchiveCloner）。取不下来时归档现场一个字不动。
        var clone = await ArchiveCloner.CloneAsync(
            LibraryRoot, record.Remote, record.Branch, stage, progress, cancellation);
        if (!clone.Success)
        {
            if (Directory.Exists(stage)) ProjectRepoLayout.DeleteTree(stage);
            return (false, $"拉取失败，归档现场未改变:\n{clone.Message}");
        }
        var lfsNote = clone.Message;
        try
        {
            progress?.Report("恢复本地 z/Z 文件夹...");
            foreach (var z in ReadZFolderNames(target))
            {
                var destination = Path.Combine(stage, z);
                if (Directory.Exists(destination)) ProjectRepoLayout.DeleteTree(destination);
                ProjectRepoLayout.CopyDirectory(Path.Combine(target, z), destination);
            }
            Directory.Move(target, backup);
            try
            {
                Directory.Move(stage, target);
            }
            catch
            {
                Directory.Move(backup, target);
                throw;
            }
            try
            {
                _lifecycle.Save(record with { ZFolders = ReadZFolderNames(target), ArchivedAt = null });
            }
            catch
            {
                Directory.Move(target, stage);
                Directory.Move(backup, target);
                throw;
            }
            _tree.InvalidateCache();
            try
            {
                ProjectRepoLayout.DeleteTree(backup);
                return (true, $"远端项目已拉取，本地 z/Z 内容已优先恢复{lfsNote}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (true, $"远端项目已拉取；归档备份清理失败: {ex.Message}{lfsNote}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (Directory.Exists(stage)) ProjectRepoLayout.DeleteTree(stage);
            return (false, $"恢复归档项目失败，原现场已保留或回滚: {ex.Message}");
        }
    }

    internal static IReadOnlyList<string> ReadZFolderNames(string path)
    {
        if (!Directory.Exists(path)) return [];
        return Directory.EnumerateDirectories(path)
            .Where(item => (File.GetAttributes(item) & FileAttributes.ReparsePoint) == 0)
            .Select(item => Path.GetFileName(item)!)
            .Where(item => item.Length > 0 && (item[0] == 'z' || item[0] == 'Z'))
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<string> ReadRefAsync(
        string repo, string reference, CancellationToken cancellation)
    {
        var result = await GitRunner.RunAsync(repo,
            ["rev-parse", "--verify", $"{reference}^{{commit}}"], cancellation);
        return result.Success ? result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim() ?? "" : "";
    }
}
