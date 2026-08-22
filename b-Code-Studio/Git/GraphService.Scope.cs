namespace HistoryJanus.Git;

public sealed partial class GraphService
{
    private async Task<(bool Success, string Message, GraphScope? Scope)> ResolveScopeAsync(
        string name, CancellationToken cancellation, bool includeRelations)
    {
        name = name.Trim();
        if (name.Length == 0)
            return (false, "项目名称不能为空", null);

        var resolved = await _projects.ResolveWorktreeAsync(name);
        if (!resolved.Success || resolved.Worktree == null)
            return (false, resolved.Message, null);
        var repo = resolved.Worktree.WorktreePath;

        var headTask = ResolveCommitAsync(repo, $"refs/heads/{ProjectService.MainlineBranch}", cancellation);
        var listedTask = ListRefsAsync(repo, cancellation);
        await Task.WhenAll(headTask, listedTask);
        var head = await headTask
                   ?? await ResolveCommitAsync(repo, "HEAD", cancellation);
        if (head == null)
            return (false, $"项目主线分支不存在: {ProjectService.MainlineBranch}", null);

        var listed = await listedTask;
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
        var candidates = SelectParallelCandidates(name, listed.Refs)
            .Where(candidate =>
                candidate.TargetSha.Length > 0 &&
                !candidate.TargetSha.Equals(mainline.TargetSha, StringComparison.OrdinalIgnoreCase) &&
                !firstParent.Contains(candidate.TargetSha))
            .ToList();
        var aheadByRef = await ReadAheadCountsAsync(
            repo, mainline.FullName, candidates.Select(item => item.FullName), cancellation);
        var parallels = candidates.Select(candidate => candidate with
        {
            Kind = IsAiWork(name, candidate.Name, candidate.FullName)
                ? BranchKind.AiWork
                : BranchKind.Parallel,
            IsOpen = aheadByRef.GetValueOrDefault(candidate.FullName) > 0,
        }).ToList();

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
        var relations = includeRelations
            ? await BuildRelationsAsync(repo, name, mainline, parentName, cutoffSha, parallels, cancellation)
            : [];
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
        var extras = await Task.WhenAll(parallels.Select(async parallel =>
        {
            var other = parallel.FullName.Length > 0 ? parallel.FullName : parallel.TargetSha;
            return new GraphBranchRelation
            {
                BranchName = parallel.Name,
                Kind = parallel.Kind,
                ParentBranch = project,
                BaselineSha = other.Length == 0
                    ? ""
                    : await MergeBaseOrEmptyAsync(repo, mainline.FullName, other, cancellation),
            };
        }));
        relations.AddRange(extras);
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
        var args = new List<string> { "log", "--merges", "--max-count=400", "--format=%P", mainlineRef };
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
        var args = new List<string> { "rev-list", "--first-parent", "--max-count=2000", rev };
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

    /// <summary>
    /// 一次 for-each-ref 读出各平行分支相对主线的 ahead。Git 2.41+ 的
    /// %(ahead-behind) 避免对每条 AI 分支再跑一次 rev-list --count。
    /// </summary>
    private async Task<Dictionary<string, int>> ReadAheadCountsAsync(
        string repo,
        string mainlineRef,
        IEnumerable<string> otherRefs,
        CancellationToken cancellation)
    {
        var names = otherRefs
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (names.Count == 0)
            return counts;

        var args = new List<string>
        {
            "for-each-ref",
            $"--format=%(refname)%09%(ahead-behind:{mainlineRef})",
        };
        args.AddRange(names);
        var result = await GitRunner.RunAsync(repo, args, cancellation: cancellation);
        if (result.Success)
        {
            foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.TrimEnd('\r').Split('\t');
                if (parts.Length < 2)
                    continue;
                var numbers = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (numbers.Length == 0 || !int.TryParse(numbers[0], out var ahead))
                    continue;
                counts[parts[0].Trim()] = ahead;
            }

            if (counts.Count > 0 || names.Count == 0)
                return counts;
        }

        var fallback = await Task.WhenAll(names.Select(async name =>
        {
            var count = await CountAheadAsync(repo, mainlineRef, name, cancellation);
            return (name, count);
        }));
        foreach (var item in fallback)
            counts[item.name] = item.count;
        return counts;
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
