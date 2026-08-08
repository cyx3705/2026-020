using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HistoryJanus.Git;

public sealed record GitFileRuleInfo(
    string Pattern,
    bool Track,
    bool Lfs,
    bool Lf,
    bool Managed,
    int FileCount,
    int TrackedCount,
    int IgnoredCount,
    int LfsAttributeCount,
    int LfsPointerCount,
    int LfAttributeCount,
    string Status);

/// <summary>项目根已声明的一条格式规则，供台账扫描消费。</summary>
public sealed record DeclaredRule(string Pattern, bool Track, bool Lfs, bool Lf, bool Managed);

public sealed record GitFileRulePreview(
    string Project,
    string Pattern,
    bool? Track,
    bool? Lfs,
    bool? Lf,
    string GitIgnoreDiff,
    string GitAttributesDiff,
    int AffectedFiles,
    int AddToIndex,
    int RemoveFromIndex,
    int Renormalize,
    bool Changed,
    bool Applied);

public sealed record GitFileRuleChange(string Pattern, bool Track, bool Lfs, bool Lf);

public sealed record GitFileRuleBatchItem(
    string Pattern,
    bool Track,
    bool Lfs,
    bool Lf,
    int AffectedFiles,
    int AddToIndex,
    int RemoveFromIndex,
    int Renormalize,
    bool Changed,
    bool Applied);

public sealed record GitFileRuleBatchPreview(
    string Project,
    IReadOnlyList<GitFileRuleBatchItem> Items,
    string GitIgnoreDiff,
    string GitAttributesDiff,
    int AddToIndex,
    int RemoveFromIndex,
    int Renormalize,
    bool Changed,
    bool Applied);

/// <summary>
/// 项目根文件格式规则：.gitignore 决定是否跟踪，.gitattributes 只生成标准 LFS/LF 行。
/// 注释块外内容保持；索引操作只使用 Git 枚举出的精确相对路径。
/// </summary>
public sealed partial class GitFileRuleService
{
    private const string ManagedBegin = "# HistoryJanus managed begin";
    private const string ManagedEnd = "# HistoryJanus managed end";

    /// <summary>
    /// 基线块由 janus.gitrule.sync 从模板整块重刷，属于机器所有内容。
    /// 与 managed 块(本项目特例)分离,基线更新不伤项目自身决定;两者都在块外内容之外。
    /// </summary>
    private const string BaselineBegin = "# HistoryJanus baseline begin";
    private const string BaselineEnd = "# HistoryJanus baseline end";
    private const string LfsAttributes = "filter=lfs diff=lfs merge=lfs -text";
    private const string LfAttributes = "text eol=lf";
    private const int GitBatchSize = 100;

    private readonly ProjectService _projects;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public GitFileRuleService(ProjectService projects) => _projects = projects;

    /// <summary>
    /// 基线同步：把模板项目根规则文件的托管内容
    /// 整块刷入目标项目的 baseline 块;项目自身的 managed 块与块外手写内容不动。
    /// apply=false 只预览。省略 project 则同步全部工作树(模板自身除外)。
    /// </summary>
    public async Task<(bool Success, string Message)> SyncBaselineAsync(
        string? project,
        bool apply,
        IProgress<string>? progress,
        CancellationToken cancellation = default)
    {
        var templateName = _projects.BaseBranch;
        var template = await _projects.ResolveWorktreeAsync(templateName).ConfigureAwait(false);
        if (!template.Success || template.Worktree == null)
            return (false, $"未找到模板项目 {templateName}: {template.Message}");

        var templateRoot = template.Worktree.WorktreePath;
        var templateDocs = await ReadDocumentsAsync(templateRoot, cancellation).ConfigureAwait(false);
        var ignoreBaseline = ManagedLines(templateDocs.Ignore);
        var attrBaseline = ManagedLines(templateDocs.Attributes);
        if (ignoreBaseline.Count == 0 && attrBaseline.Count == 0)
            return (false, $"模板 {templateName} 的托管块为空,先用 janus.gitrule.set 在模板上建立基线");

        List<WorktreeInfo> targets;
        if (!string.IsNullOrWhiteSpace(project))
        {
            var one = await _projects.ResolveWorktreeAsync(project).ConfigureAwait(false);
            if (!one.Success || one.Worktree == null)
                return (false, one.Message);
            targets = [one.Worktree];
        }
        else
        {
            var (git, worktrees) = await _projects.ListWorktreesAsync().ConfigureAwait(false);
            if (!git.Success)
                return (false, $"获取工作树清单失败:\n{git.Output}");
            targets = worktrees
                .Where(w => Directory.Exists(w.WorktreePath)
                            && !w.BranchName.Equals(templateName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var text = new StringBuilder();
        text.Append($"基线同步({(apply ? "已写入" : "预览")}): 源 = {templateName} " +
                    $"({ignoreBaseline.Count} 条忽略 / {attrBaseline.Count} 条属性),目标 {targets.Count} 个项目");

        var changed = 0;
        var failed = 0;
        await _writeGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            foreach (var target in targets)
            {
                progress?.Report($"基线同步 {target.BranchName} ...");
                var docs = await ReadDocumentsAsync(target.WorktreePath, cancellation).ConfigureAwait(false);
                var newIgnore = RewriteBaselineBlock(docs.Ignore, ignoreBaseline);
                var newAttr = RewriteBaselineBlock(docs.Attributes, attrBaseline);
                var ignoreDiffers = newIgnore != docs.Ignore.Text;
                var attrDiffers = newAttr != docs.Attributes.Text;

                if (!ignoreDiffers && !attrDiffers)
                    continue;

                changed++;
                if (!apply)
                {
                    text.Append($"\n  {target.BranchName}: 待更新" +
                                $"{(ignoreDiffers ? " .gitignore" : "")}{(attrDiffers ? " .gitattributes" : "")}");
                    continue;
                }

                var write = await WriteDocumentsAsync(docs, newIgnore, newAttr, cancellation).ConfigureAwait(false);
                if (write.Success)
                {
                    text.Append($"\n  {target.BranchName}: ✓ 基线块已刷新");
                }
                else
                {
                    failed++;
                    text.Append($"\n  {target.BranchName}: ✗ {write.Message}");
                }
            }
        }
        finally
        {
            _writeGate.Release();
        }

        text.Append(changed == 0
            ? "\n全部项目基线已是最新,无需变更"
            : apply
                ? $"\n完成: {changed - failed}/{changed} 个项目已更新" +
                  (failed > 0 ? $",{failed} 个失败" : "") + ";项目自身 managed 块与块外内容未动"
                : "\napply=true 写入(经二次确认)");
        return (failed == 0, text.ToString());
    }

    /// <summary>
    /// 读取项目根已声明的格式规则，供台账扫描复用并保持规则解析只有一份实现。
    /// 只回声明,不查索引——调用方若需归宿判定,应以 git ls-files 的三态为权威。
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, DeclaredRule>> ReadDeclaredRulesAsync(
        string root,
        CancellationToken cancellation = default)
    {
        var documents = await ReadDocumentsAsync(root, cancellation).ConfigureAwait(false);
        return ReadDefinitions(documents.Ignore, documents.Attributes).ToDictionary(
            entry => entry.Key,
            entry => new DeclaredRule(
                entry.Value.Pattern, entry.Value.Track, entry.Value.Lfs, entry.Value.Lf, entry.Value.Managed),
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<(bool Success, string Message, IReadOnlyList<GitFileRuleInfo> Rules)> ListAsync(
        string project,
        CancellationToken cancellation = default)
    {
        var resolved = await ResolveAsync(project).ConfigureAwait(false);
        if (!resolved.Success)
            return (false, resolved.Message, []);

        var documents = await ReadDocumentsAsync(resolved.Root!, cancellation).ConfigureAwait(false);
        var definitions = ReadDefinitions(documents.Ignore, documents.Attributes);
        var repository = await ReadRepositoryStateAsync(resolved.Root!, definitions.Keys, cancellation)
            .ConfigureAwait(false);
        if (!repository.Success)
            return (false, repository.Message, []);

        var rows = BuildRows(definitions, repository);
        return (true, $"{project}: {rows.Count} 条文件格式规则", rows);
    }

    public async Task<(bool Success, string Message, GitFileRulePreview? Preview)> SetAsync(
        string project,
        string pattern,
        bool track,
        bool lfs,
        bool lf,
        bool apply,
        CancellationToken cancellation = default)
    {
        try
        {
            pattern = ValidatePattern(pattern);
        }
        catch (InvalidOperationException ex)
        {
            return (false, ex.Message, null);
        }
        if (!track && (lfs || lf))
            return (false, "不纳入 Git 时 LFS 和 LF 必须同时关闭", null);
        if (lfs && lf)
            return (false, "LFS 与 LF 互斥，不能同时开启", null);
        // 目录规则只表达“忽略”，不生成 .gitattributes 行；目录级 LFS/LF 没有意义。
        if (LooksLikeDirectoryPattern(pattern) && (track || lfs || lf))
            return (false, "目录规则只能用于忽略:请设 track=false lfs=false lf=false", null);

        await _writeGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var resolved = await ResolveAsync(project).ConfigureAwait(false);
            if (!resolved.Success)
                return (false, resolved.Message, null);
            return await SetCoreAsync(
                project, resolved.Root!, pattern, track, lfs, lf, apply, cancellation).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<(bool Success, string Message, GitFileRulePreview? Preview)> RemoveAsync(
        string project,
        string pattern,
        bool apply,
        CancellationToken cancellation = default)
    {
        try
        {
            pattern = ValidatePattern(pattern);
        }
        catch (InvalidOperationException ex)
        {
            return (false, ex.Message, null);
        }
        await _writeGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var resolved = await ResolveAsync(project).ConfigureAwait(false);
            if (!resolved.Success)
                return (false, resolved.Message, null);
            return await RemoveCoreAsync(project, resolved.Root!, pattern, apply, cancellation)
                .ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<(bool Success, string Message, GitFileRulePreview? Preview)> SetCoreAsync(
        string project,
        string root,
        string pattern,
        bool track,
        bool lfs,
        bool lf,
        bool apply,
        CancellationToken cancellation)
    {
        var documents = await ReadDocumentsAsync(root, cancellation).ConfigureAwait(false);
        var ignoreManaged = ManagedLines(documents.Ignore)
            .Where(line => !IgnorePatternEquals(line, pattern)).ToList();
        if (!track)
            ignoreManaged.Add(pattern);

        var attributeManaged = ManagedLines(documents.Attributes)
            .Where(line => !AttributePatternEquals(line, pattern)).ToList();
        if (track && lfs)
            attributeManaged.Add($"{pattern} {LfsAttributes}");
        else if (track && lf)
            attributeManaged.Add($"{pattern} {LfAttributes}");

        var newIgnore = RewriteDocument(
            documents.Ignore, ignoreManaged,
            line => IgnorePatternEquals(line, pattern));
        var newAttributes = RewriteDocument(
            documents.Attributes, attributeManaged,
            line => IsCanonicalAttributeForPattern(line, pattern));

        var definitions = ReadDefinitions(documents.Ignore, documents.Attributes);
        definitions[pattern] = new RuleDefinition(pattern, track, lfs, lf, true);
        var repository = await ReadRepositoryStateAsync(root, definitions.Keys, cancellation)
            .ConfigureAwait(false);
        if (!repository.Success)
            return (false, repository.Message, null);

        var affected = repository.AllFiles.Where(path => MatchesPattern(pattern, path)).ToList();
        var tracked = affected.Count(path => repository.Tracked.Contains(path));
        var add = track ? affected.Count - tracked : 0;
        var remove = track ? 0 : tracked;
        var renormalize = track ? tracked : 0;
        var filesChanged = documents.Ignore.Text != newIgnore || documents.Attributes.Text != newAttributes;
        var indexChanged = add > 0 || remove > 0 || (track && (lfs || lf) && renormalize > 0);
        var preview = new GitFileRulePreview(
            project, pattern, track, lfs, lf,
            BuildDiff(".gitignore", documents.Ignore.Text, newIgnore),
            BuildDiff(".gitattributes", documents.Attributes.Text, newAttributes),
            affected.Count, add, remove, renormalize,
            filesChanged || indexChanged, false);

        if (!apply || !preview.Changed)
            return (true, PreviewMessage(preview, apply: false), preview);

        var write = await WriteDocumentsAsync(
            documents, newIgnore, newAttributes, cancellation).ConfigureAwait(false);
        if (!write.Success)
            return (false, write.Message, preview);

        var sync = await SynchronizeSetAsync(root, pattern, track, cancellation).ConfigureAwait(false);
        if (!sync.Success)
            return (false, $"规则已写入，但索引同步失败：\n{sync.Output}", preview with { Applied = true });

        return (true, PreviewMessage(preview, apply: true), preview with { Applied = true });
    }

    private async Task<(bool Success, string Message, GitFileRulePreview? Preview)> RemoveCoreAsync(
        string project,
        string root,
        string pattern,
        bool apply,
        CancellationToken cancellation)
    {
        var documents = await ReadDocumentsAsync(root, cancellation).ConfigureAwait(false);
        var newIgnore = RewriteDocument(
            documents.Ignore,
            ManagedLines(documents.Ignore).Where(line => !IgnorePatternEquals(line, pattern)).ToList(),
            line => IgnorePatternEquals(line, pattern));
        var newAttributes = RewriteDocument(
            documents.Attributes,
            ManagedLines(documents.Attributes).Where(line => !AttributePatternEquals(line, pattern)).ToList(),
            line => IsCanonicalAttributeForPattern(line, pattern));

        var definitions = ReadDefinitions(documents.Ignore, documents.Attributes);
        var repository = await ReadRepositoryStateAsync(root, definitions.Keys.Append(pattern), cancellation)
            .ConfigureAwait(false);
        if (!repository.Success)
            return (false, repository.Message, null);
        var affected = repository.AllFiles.Where(path => MatchesPattern(pattern, path)).ToList();
        var tracked = affected.Count(path => repository.Tracked.Contains(path));
        var changed = documents.Ignore.Text != newIgnore || documents.Attributes.Text != newAttributes;
        var preview = new GitFileRulePreview(
            project, pattern, null, null, null,
            BuildDiff(".gitignore", documents.Ignore.Text, newIgnore),
            BuildDiff(".gitattributes", documents.Attributes.Text, newAttributes),
            affected.Count, 0, 0, tracked, changed, false);

        if (!apply || !changed)
            return (true, PreviewMessage(preview, apply: false), preview);

        var write = await WriteDocumentsAsync(
            documents, newIgnore, newAttributes, cancellation).ConfigureAwait(false);
        if (!write.Success)
            return (false, write.Message, preview);
        var trackedExisting = affected
            .Where(path => repository.Tracked.Contains(path) && File.Exists(ToAbsolute(root, path)))
            .ToList();
        var normalize = await RunBatchesAsync(
            root, ["add", "--renormalize", "--"], trackedExisting, cancellation).ConfigureAwait(false);
        if (!normalize.Success)
            return (false, $"规则已删除，但索引重新写入失败：\n{normalize.Output}", preview with { Applied = true });
        var stage = await StageRuleFilesAsync(root, cancellation).ConfigureAwait(false);
        if (!stage.Success)
            return (false, $"规则已删除，但规则文件暂存失败：\n{stage.Output}", preview with { Applied = true });

        return (true, PreviewMessage(preview, apply: true), preview with { Applied = true });
    }

}
