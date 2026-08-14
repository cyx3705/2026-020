namespace HistoryJanus.Git;

public sealed partial class GraphService
{
    private async Task<(bool Success, string Message, GraphScope? Scope)> ResolveScopeAsync(
        string name, CancellationToken cancellation)
    {
        name = name.Trim();
        if (name.Length == 0)
            return (false, "项目名称不能为空", null);

        var resolved = await _projects.ResolveWorktreeAsync(name);
        if (!resolved.Success || resolved.Worktree == null)
            return (false, resolved.Message, null);
        var repo = resolved.Worktree.WorktreePath;

        var head = await ResolveCommitAsync(repo, $"refs/heads/{ProjectService.MainlineBranch}", cancellation)
                   ?? await ResolveCommitAsync(repo, "HEAD", cancellation);
        if (head == null)
            return (false, $"项目主线分支不存在: {ProjectService.MainlineBranch}", null);

        var listed = await ListRefsAsync(repo, cancellation);
        if (!listed.Success)
            return (false, listed.Message, null);

        var mainline = new GraphRef
        {
            Name = ProjectService.MainlineBranch,
            FullName = $"refs/heads/{ProjectService.MainlineBranch}",
            IsRemote = false,
            TargetSha = head,
            Kind = BranchKind.Mainline,
            IsOpen = true,
        };

        var parentName = ProjectRepoLayout.ReadTemplateSource(repo) ?? _projects.BaseBranch;
        if (parentName.Equals(name, StringComparison.OrdinalIgnoreCase))
            parentName = _projects.BaseBranch;
        const string cutoffSha = "";

        var firstParent = await ReadFirstParentShasAsync(repo, mainline.FullName, cutoffSha, cancellation);
        var parallels = new List<GraphRef>();
        foreach (var candidate in SelectParallelCandidates(name, listed.Refs))
        {
            if (candidate.TargetSha.Length == 0 ||
                candidate.TargetSha.Equals(mainline.TargetSha, StringComparison.OrdinalIgnoreCase) ||
                firstParent.Contains(candidate.TargetSha))
                continue;

            var ahead = await CountAheadAsync(repo, mainline.FullName, candidate.FullName, cancellation);
            parallels.Add(candidate with
            {
                Kind = IsAiWork(name, candidate.Name, candidate.FullName)
                    ? BranchKind.AiWork
                    : BranchKind.Parallel,
                IsOpen = ahead > 0,
            });
        }

        var covered = parallels
            .Select(item => item.TargetSha)
            .Where(sha => sha.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var recovered in await DiscoverMergedHistoriesAsync(
                     repo, mainline.FullName, cutoffSha, firstParent, covered, cancellation))
            parallels.Add(recovered);

        parallels = parallels
            .OrderByDescending(item => item.IsOpen)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var allRefs = new List<GraphRef> { mainline };
        allRefs.AddRange(parallels.Where(item => item.FullName.Length > 0));
        var relations = await BuildRelationsAsync(repo, name, mainline, parentName, cutoffSha, parallels, cancellation);
        return (true, "范围已解析", new GraphScope(name, mainline, parallels, allRefs, relations, cutoffSha, repo));
    }

    private async Task<List<GraphBranchRelation>> BuildRelationsAsync(
        string repo,
        string project,
        GraphRef mainline,
        string parentName,
        string cutoffSha,
        List<GraphRef> parallels,
        CancellationToken cancellation)
    {
        var relations = new List<GraphBranchRelation>
        {
            new()
            {
                BranchName = mainline.Name,
                Kind = BranchKind.Mainline,
                ParentBranch = parentName.Equals(project, StringComparison.OrdinalIgnoreCase) ? "" : parentName,
                BaselineSha = cutoffSha,
            },
        };
        foreach (var parallel in parallels)
        {
            var other = parallel.FullName.Length > 0 ? parallel.FullName : parallel.TargetSha;
            relations.Add(new GraphBranchRelation
            {
                BranchName = parallel.Name,
                Kind = parallel.Kind,
                ParentBranch = project,
                BaselineSha = other.Length == 0
                    ? ""
                    : await MergeBaseOrEmptyAsync(repo, mainline.FullName, other, cancellation),
            });
        }

        return relations;
    }

    private static List<GraphRef> SelectParallelCandidates(string project, IReadOnlyList<RawRef> refs)
    {
        var chosen = new Dictionary<string, GraphRef>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in refs)
        {
            var logical = LogicalRefName(item);
            if (!IsAiWork(project, logical, item.FullName))
                continue;
            if (IsExcludedProjectRef(project, logical))
                continue;

            var candidate = new GraphRef
            {
                Name = logical,
                FullName = item.FullName,
                IsRemote = item.IsRemote,
                TargetSha = item.Sha,
                Kind = BranchKind.AiWork,
                IsOpen = true,
            };
            if (!chosen.TryGetValue(logical, out var existing) || (existing.IsRemote && !candidate.IsRemote))
                chosen[logical] = candidate;
        }

        return chosen.Values.ToList();
    }

    /// <summary>
    /// 已删除但仍被 --no-ff 合并提交第二父指向的历史：ref 没了，提交对象还在主线可达集里。
    /// squash / fast-forward 没有第二父，无法还原平行行。
    /// </summary>
    private async Task<List<GraphRef>> DiscoverMergedHistoriesAsync(
        string repo,
        string mainlineRef,
        string cutoffSha,
        HashSet<string> firstParent,
        HashSet<string> alreadyCovered,
        CancellationToken cancellation)
    {
        var args = new List<string> { "log", "--merges", "--format=%P", mainlineRef };
        if (!string.IsNullOrWhiteSpace(cutoffSha))
        {
            args.Add("--not");
            args.Add(cutoffSha);
        }

        var result = await GitRunner.RunAsync(repo, args, cancellation: cancellation);
        if (!result.Success)
            return [];

        var recovered = new List<GraphRef>();
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parents = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 1; i < parents.Length; i++)
            {
                var sha = parents[i];
                if (sha.Length == 0 ||
                    firstParent.Contains(sha) ||
                    alreadyCovered.Contains(sha))
                    continue;
                alreadyCovered.Add(sha);
                recovered.Add(new GraphRef
                {
                    Name = $"merged/{Short(sha)}",
                    FullName = "",
                    IsRemote = false,
                    TargetSha = sha,
                    Kind = BranchKind.Parallel,
                    IsOpen = false,
                });
            }
        }

        return recovered;
    }

    private async Task<HashSet<string>> ReadFirstParentShasAsync(
        string repo, string rev, string cutoffSha, CancellationToken cancellation)
    {
        var args = new List<string> { "rev-list", "--first-parent", rev };
        if (!string.IsNullOrWhiteSpace(cutoffSha))
        {
            args.Add("--not");
            args.Add(cutoffSha);
        }

        var result = await GitRunner.RunAsync(repo, args, cancellation: cancellation);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!result.Success)
            return set;
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var sha = line.Trim();
            if (sha.Length > 0)
                set.Add(sha);
        }

        return set;
    }

    private async Task<int> CountAheadAsync(
        string repo, string mainlineRef, string otherRef, CancellationToken cancellation)
    {
        var result = await GitRunner.RunAsync(repo,
            ["rev-list", "--count", $"{mainlineRef}..{otherRef}"], cancellation: cancellation);
        if (!result.Success)
            return 0;
        var text = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim();
        return int.TryParse(text, out var count) ? count : 0;
    }

    private static bool IsAiWork(string project, string shortName, string fullName)
    {
        var prefix = $"ai/{project}/";
        if (shortName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return true;
        if (fullName.StartsWith($"refs/heads/{prefix}", StringComparison.OrdinalIgnoreCase))
            return true;
        if (shortName.StartsWith($"origin/{prefix}", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!fullName.StartsWith("refs/remotes/", StringComparison.OrdinalIgnoreCase))
            return false;
        var rest = fullName["refs/remotes/".Length..];
        var slash = rest.IndexOf('/');
        return slash >= 0
               && rest[(slash + 1)..].StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string LogicalRefName(RawRef item)
    {
        if (!item.IsRemote)
            return item.ShortName;
        if (!item.FullName.StartsWith("refs/remotes/", StringComparison.OrdinalIgnoreCase))
            return item.ShortName;
        var rest = item.FullName["refs/remotes/".Length..];
        var slash = rest.IndexOf('/');
        return slash >= 0 ? rest[(slash + 1)..] : item.ShortName;
    }

    private static bool IsExcludedProjectRef(string project, string logicalName)
        => logicalName.Equals(project, StringComparison.OrdinalIgnoreCase)
           || logicalName.Equals(ProjectService.MainlineBranch, StringComparison.OrdinalIgnoreCase);
}
