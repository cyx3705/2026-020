using System.IO;

namespace HistoryJanus.Git;

public sealed partial class GitFileRuleService
{
    public async Task<(bool Success, string Message, GitFileRuleBatchPreview? Preview)> BatchSetAsync(
        string project,
        IReadOnlyList<GitFileRuleChange> changes,
        bool apply,
        CancellationToken cancellation = default)
    {
        if (changes.Count is < 1 or > 500)
            return (false, "批量规则必须包含 1 至 500 项", null);

        var normalized = new List<GitFileRuleChange>(changes.Count);
        var patterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            string pattern;
            try
            {
                pattern = ValidatePattern(change.Pattern);
            }
            catch (InvalidOperationException ex)
            {
                return (false, ex.Message, null);
            }
            if (!patterns.Add(pattern))
                return (false, $"批量规则包含重复 Pattern: {pattern}", null);
            if (!change.Track && (change.Lfs || change.Lf))
                return (false, $"{pattern}: 不纳入 Git 时 LFS 和 LF 必须同时关闭", null);
            if (change.Lfs && change.Lf)
                return (false, $"{pattern}: LFS 与 LF 互斥，不能同时开启", null);
            if (LooksLikeDirectoryPattern(pattern) && (change.Track || change.Lfs || change.Lf))
                return (false, $"{pattern}: 目录规则只能用于忽略", null);
            normalized.Add(change with { Pattern = pattern });
        }

        normalized.Sort((left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.Pattern, right.Pattern));
        await _writeGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var resolved = await ResolveAsync(project).ConfigureAwait(false);
            if (!resolved.Success)
                return (false, resolved.Message, null);
            return await BatchSetCoreAsync(project, resolved.Root!, normalized, apply, cancellation)
                .ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<(bool Success, string Message, GitFileRuleBatchPreview? Preview)> BatchSetCoreAsync(
        string project,
        string root,
        IReadOnlyList<GitFileRuleChange> changes,
        bool apply,
        CancellationToken cancellation)
    {
        var documents = await ReadDocumentsAsync(root, cancellation).ConfigureAwait(false);
        var ignoreManaged = ManagedLines(documents.Ignore).ToList();
        var attributeManaged = ManagedLines(documents.Attributes).ToList();
        var changedPatterns = changes.Select(change => change.Pattern)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var change in changes)
        {
            ignoreManaged.RemoveAll(line => IgnorePatternEquals(line, change.Pattern));
            attributeManaged.RemoveAll(line => AttributePatternEquals(line, change.Pattern));
            if (!change.Track)
                ignoreManaged.Add(change.Pattern);
            else if (change.Lfs)
                attributeManaged.Add($"{change.Pattern} {LfsAttributes}");
            else if (change.Lf)
                attributeManaged.Add($"{change.Pattern} {LfAttributes}");
        }

        var newIgnore = RewriteDocument(
            documents.Ignore, ignoreManaged,
            line => changedPatterns.Any(pattern => IgnorePatternEquals(line, pattern)));
        var newAttributes = RewriteDocument(
            documents.Attributes, attributeManaged,
            line => changedPatterns.Any(pattern => IsCanonicalAttributeForPattern(line, pattern)));

        var definitions = ReadDefinitions(documents.Ignore, documents.Attributes);
        foreach (var change in changes)
        {
            definitions[change.Pattern] = new RuleDefinition(
                change.Pattern, change.Track, change.Lfs, change.Lf, true);
        }
        var repository = await ReadRepositoryStateAsync(root, definitions.Keys, cancellation)
            .ConfigureAwait(false);
        if (!repository.Success)
            return (false, repository.Message, null);

        var items = new List<GitFileRuleBatchItem>(changes.Count);
        foreach (var change in changes)
        {
            var affected = repository.AllFiles.Where(path => MatchesPattern(change.Pattern, path)).ToList();
            var tracked = affected.Count(path => repository.Tracked.Contains(path));

            var oneIgnore = ManagedLines(documents.Ignore)
                .Where(line => !IgnorePatternEquals(line, change.Pattern)).ToList();
            if (!change.Track)
                oneIgnore.Add(change.Pattern);
            var oneAttributes = ManagedLines(documents.Attributes)
                .Where(line => !AttributePatternEquals(line, change.Pattern)).ToList();
            if (change.Track && change.Lfs)
                oneAttributes.Add($"{change.Pattern} {LfsAttributes}");
            else if (change.Track && change.Lf)
                oneAttributes.Add($"{change.Pattern} {LfAttributes}");
            var filesChanged = documents.Ignore.Text != RewriteDocument(
                                   documents.Ignore, oneIgnore,
                                   line => IgnorePatternEquals(line, change.Pattern))
                               || documents.Attributes.Text != RewriteDocument(
                                   documents.Attributes, oneAttributes,
                                   line => IsCanonicalAttributeForPattern(line, change.Pattern));
            var add = change.Track ? affected.Count - tracked : 0;
            var remove = change.Track ? 0 : tracked;
            var renormalize = affected.Count(path => repository.Tracked.Contains(path)
                && NeedsRenormalize(change, path, repository));
            items.Add(new GitFileRuleBatchItem(
                change.Pattern, change.Track, change.Lfs, change.Lf,
                affected.Count, add, remove, renormalize,
                filesChanged || add > 0 || remove > 0 || renormalize > 0, false));
        }

        var addPaths = BatchPaths(changes.Where(change => change.Track), repository, tracked: false);
        var removePaths = BatchPaths(changes.Where(change => !change.Track), repository, tracked: true);
        var normalizePaths = changes.Where(change => change.Track)
            .SelectMany(change => repository.AllFiles.Where(path => repository.Tracked.Contains(path)
                && MatchesPattern(change.Pattern, path) && NeedsRenormalize(change, path, repository)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var preview = new GitFileRuleBatchPreview(
            project, items,
            BuildDiff(".gitignore", documents.Ignore.Text, newIgnore),
            BuildDiff(".gitattributes", documents.Attributes.Text, newAttributes),
            addPaths.Count, removePaths.Count, normalizePaths.Count,
            documents.Ignore.Text != newIgnore || documents.Attributes.Text != newAttributes
            || addPaths.Count > 0 || removePaths.Count > 0 || normalizePaths.Count > 0,
            false);

        if (!apply || !preview.Changed)
            return (true, BatchPreviewMessage(preview, applied: false), preview);

        var write = await WriteDocumentsAsync(documents, newIgnore, newAttributes, cancellation)
            .ConfigureAwait(false);
        if (!write.Success)
            return (false, write.Message, preview);

        var sync = await SynchronizeBatchAsync(root, changes, repository, cancellation).ConfigureAwait(false);
        if (!sync.Success)
            return (false, $"规则已写入，但批量索引同步失败：\n{sync.Output}", preview);

        var appliedItems = items.Select(item => item with { Applied = item.Changed }).ToList();
        var appliedPreview = preview with { Items = appliedItems, Applied = true };
        return (true, BatchPreviewMessage(appliedPreview, applied: true), appliedPreview);
    }

    private static HashSet<string> BatchPaths(
        IEnumerable<GitFileRuleChange> changes,
        RepositoryState repository,
        bool tracked)
    {
        var patterns = changes.Select(change => change.Pattern).ToList();
        return repository.AllFiles.Where(path => repository.Tracked.Contains(path) == tracked
            && patterns.Any(pattern => MatchesPattern(pattern, path)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool NeedsRenormalize(
        GitFileRuleChange change,
        string path,
        RepositoryState repository)
    {
        var attributes = repository.Attributes.GetValueOrDefault(path);
        var hasLfs = string.Equals(
            attributes?.GetValueOrDefault("filter"), "lfs", StringComparison.OrdinalIgnoreCase);
        var hasLf = string.Equals(
                        attributes?.GetValueOrDefault("text"), "set", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        attributes?.GetValueOrDefault("eol"), "lf", StringComparison.OrdinalIgnoreCase);
        if (change.Lfs)
            return !hasLfs || !repository.LfsPointers.Contains(path);
        if (change.Lf)
            return !hasLf;
        return hasLfs || hasLf;
    }

    private static async Task<GitResult> SynchronizeBatchAsync(
        string root,
        IReadOnlyList<GitFileRuleChange> changes,
        RepositoryState repository,
        CancellationToken cancellation)
    {
        var remove = BatchPaths(changes.Where(change => !change.Track), repository, tracked: true);
        var result = await RunBatchesAsync(
            root, ["rm", "--cached", "--ignore-unmatch", "--"], remove.ToList(), cancellation)
            .ConfigureAwait(false);
        if (!result.Success)
            return result;

        var normalize = BatchPaths(changes.Where(change => change.Track), repository, tracked: true);
        normalize.ExceptWith(remove);
        result = await RunBatchesAsync(
            root, ["add", "--renormalize", "--"],
            normalize.Where(path => File.Exists(ToAbsolute(root, path))).ToList(), cancellation)
            .ConfigureAwait(false);
        if (!result.Success)
            return result;

        var others = await GitRunner.RunAsync(
            root, ["ls-files", "-z", "--others", "--exclude-standard"], cancellation: cancellation)
            .ConfigureAwait(false);
        if (!others.Success)
            return others;
        var trackedPatterns = changes.Where(change => change.Track).Select(change => change.Pattern).ToList();
        var add = ParseNullPaths(others.Output)
            .Where(path => trackedPatterns.Any(pattern => MatchesPattern(pattern, path))).ToList();
        result = await RunBatchesAsync(root, ["add", "--"], add, cancellation).ConfigureAwait(false);
        if (!result.Success)
            return result;
        return await StageRuleFilesAsync(root, cancellation).ConfigureAwait(false);
    }

    private static string BatchPreviewMessage(GitFileRuleBatchPreview preview, bool applied)
        => $"{(applied ? "规则与索引已批量更新" : "批量规则预览，尚未写入")}：" +
           $"{preview.Items.Count} 条规则" +
           $"\n加入索引 {preview.AddToIndex}，移出索引 {preview.RemoveFromIndex}，" +
           $"重新写入 {preview.Renormalize}" +
           $"\n{preview.GitIgnoreDiff}\n{preview.GitAttributesDiff}" +
           (preview.Changed ? string.Empty : "\n无需变化") +
           "\n不会自动 commit、push 或重写历史";

}
