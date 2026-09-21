using System.IO;
using System.Text;
using System.Text.Json;

namespace HistoryJanus.Git;

/// <summary>「LFS 规则」表的一行：一个 ≥100MB 的文件（已走指针的，或工作区里的）。</summary>
public sealed record LfsFileRow(
    string Path,
    long SizeBytes,
    string Size,
    string State,
    string Decision,
    string Note);

/// <summary>
/// 一个仓的 LFS 实况与汇总。<see cref="Available"/> 为假说明本机没装 git-lfs，
/// 这与「装了但一个文件都没走 LFS」是两回事，页面要分开说。
/// </summary>
public sealed record LfsInspection(
    string Project,
    bool Available,
    IReadOnlyList<LfsFileRow> Files,
    int PointerCount,
    long PointerBytes,
    int OversizeCount,
    int DecidedLfs,
    int DecidedIgnore,
    int Undecided,
    int SmallPointerCount,
    int MissingEntityCount,
    int ForeignRuleCount);

/// <summary>一个仓修复前后的账。</summary>
public sealed record LfsRepairReport(
    string Project,
    bool DryRun,
    int ForeignRulesStripped,
    int Converted,
    long ConvertedBytes,
    int KeptLarge,
    IReadOnlyList<string> MissingEntity,
    int Commits,
    string Detail);

/// <summary>≥100MB 文件的决定。</summary>
public enum LfsDecision
{
    None,
    Lfs,
    Ignore,
}

/// <summary>
/// 按仓执行 LFS 规则（5.11.0）：
/// 不到 100MB 的文件一律不走 LFS；≥100MB 的文件由人决定走指针还是不再纳入 git，
/// 决定写在仓里的两个托管块，**下次提交时生效，历史不改写**。
///
/// 规则的纯函数部分在 <see cref="LfsPolicy"/>；这里只负责读仓、跑 git、写文件。
/// </summary>
public sealed class LfsRuleService
{
    private const string AttributesFile = ".gitattributes";
    private const string IgnoreFile = ".gitignore";

    private readonly ProjectService _projects;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public LfsRuleService(ProjectService projects) => _projects = projects;

    // ---------------------------------------------------------------- 只读

    public async Task<(bool Success, string Message, LfsInspection? Report)> InspectAsync(
        string? project, CancellationToken cancellation = default)
    {
        var resolved = await _projects.ResolveWorktreeAsync(project ?? "").ConfigureAwait(false);
        if (!resolved.Success || resolved.Worktree == null)
            return (false, resolved.Message, null);
        var name = resolved.Worktree.BranchName;
        var root = resolved.Worktree.WorktreePath;

        if (!await WorktreeLfsHelper.IsGitLfsAvailableAsync(root).ConfigureAwait(false))
            return (true, "本机未安装 git-lfs",
                new LfsInspection(name, false, [], 0, 0, 0, 0, 0, 0, 0, 0, 0));

        var pointers = await ListPointersAsync(root, cancellation).ConfigureAwait(false);
        if (pointers == null)
            return (false, $"{name}: 读取 LFS 清单失败", null);

        var lfsDecided = ToSet(LfsPolicy.ReadLfsPaths(await ReadAsync(root, AttributesFile, cancellation)));
        var ignoreDecided = ToSet(LfsPolicy.ReadIgnoredPaths(await ReadAsync(root, IgnoreFile, cancellation)));
        var foreign = await CountForeignRulesAsync(root, cancellation).ConfigureAwait(false);

        var oversize = await Task.Run(() => WorktreeFileScanner.ScanDirectory(
            root, LfsPolicy.ThresholdBytes, LfsPolicy.ThresholdBytes).LargeFiles, cancellation)
            .ConfigureAwait(false);

        var rows = new Dictionary<string, LfsFileRow>(StringComparer.OrdinalIgnoreCase);
        int undecided = 0, small = 0, missing = 0;
        foreach (var pointer in pointers)
        {
            var large = pointer.Size >= LfsPolicy.ThresholdBytes;
            if (!pointer.Checkout) missing++;
            if (!large) small++;
            rows[pointer.Path] = new LfsFileRow(
                pointer.Path, pointer.Size, LfsPolicy.FormatBytes(pointer.Size),
                pointer.Checkout ? "LFS 指针" : "LFS 指针（本机无实体）",
                large ? Describe(pointer.Path, lfsDecided, ignoreDecided) : "—",
                large
                    ? lfsDecided.Contains(pointer.Path) ? "" : "≥100MB 但不在 lfs 托管块里：修复会补上精确路径"
                    : pointer.Checkout
                        ? "不到 100MB，不允许走 LFS：运行修复转回普通入库"
                        : "不到 100MB 且本机缺实体：修复时先 git lfs pull，取不到才暂留指针");
        }

        foreach (var file in oversize)
        {
            if (rows.ContainsKey(file.RelativePath))
                continue;
            var tracked = await IsTrackedAsync(root, file.RelativePath, cancellation).ConfigureAwait(false);
            var ignored = !tracked && await IsIgnoredAsync(root, file.RelativePath, cancellation).ConfigureAwait(false);
            var decision = Describe(file.RelativePath, lfsDecided, ignoreDecided);
            var isDecided = lfsDecided.Contains(file.RelativePath) || ignoreDecided.Contains(file.RelativePath);
            if (!isDecided && !ignored) undecided++;
            rows[file.RelativePath] = new LfsFileRow(
                file.RelativePath, file.SizeBytes, file.FormattedSize,
                tracked ? "普通入库" : ignored ? "已被排除" : "未跟踪",
                ignored && !isDecided ? "—" : decision,
                isDecided
                    ? "下次提交生效；历史不变"
                    : ignored
                        ? "已被入库规则排除，不会进仓库"
                        : "提交时会逐个弹窗确认；也可以在这里先定");
        }

        // 表里只列 ≥100MB 的文件（用户定）：小文件不该走 LFS，它们只以违规计数出现在汇总里。
        var files = rows.Values.Where(row => row.SizeBytes >= LfsPolicy.ThresholdBytes)
            .OrderByDescending(row => row.SizeBytes).ToList();
        var inspection = new LfsInspection(
            name, true, files,
            pointers.Count, pointers.Sum(pointer => pointer.Size),
            files.Count(row => row.SizeBytes >= LfsPolicy.ThresholdBytes),
            lfsDecided.Count, ignoreDecided.Count, undecided, small, missing, foreign);
        return (true, Summarize(inspection), inspection);
    }

    public static string Summarize(LfsInspection report)
    {
        if (!report.Available)
            return $"{report.Project}: 本机未安装 git-lfs";
        var text = new StringBuilder();
        text.Append($"{report.Project}: LFS 指针 {report.PointerCount} 个（{LfsPolicy.FormatBytes(report.PointerBytes)}）");
        text.Append($"；≥100MB 文件 {report.OversizeCount} 个，定为 LFS {report.DecidedLfs} / 不纳入 git {report.DecidedIgnore} / 未决定 {report.Undecided}");
        if (report.SmallPointerCount > 0 || report.ForeignRuleCount > 0)
            text.Append($"；违规：不足 100MB 的指针 {report.SmallPointerCount} 个、托管块外 LFS 规则 {report.ForeignRuleCount} 条");
        return text.ToString();
    }

    private static string Describe(string path, HashSet<string> lfs, HashSet<string> ignore)
        => lfs.Contains(path) ? "LFS 指针" : ignore.Contains(path) ? "不纳入 git" : "未决定";

    // ---------------------------------------------------------------- 决定

    /// <summary>
    /// 记住一个 ≥100MB 文件的去向。只写托管块，不动索引、不提交：
    /// 索引那一步在下次提交时由 <see cref="StageDecisionsAsync"/> 做。
    /// </summary>
    public async Task<(bool Success, string Message)> SetDecisionAsync(
        string project, string path, LfsDecision decision, CancellationToken cancellation = default)
    {
        var resolved = await _projects.ResolveWorktreeAsync(project).ConfigureAwait(false);
        if (!resolved.Success || resolved.Worktree == null)
            return (false, resolved.Message);
        var root = resolved.Worktree.WorktreePath;
        var relative = LfsPolicy.Normalize(path);
        if (relative.Length == 0 || relative.Contains("..", StringComparison.Ordinal))
            return (false, $"路径不合法：{path}");

        if (decision != LfsDecision.None)
        {
            var size = await SizeOfAsync(root, relative, cancellation).ConfigureAwait(false);
            if (size == null)
                return (false, $"{relative} 不存在于工作区");
            if (size < LfsPolicy.ThresholdBytes)
                return (false, $"{relative} 只有 {LfsPolicy.FormatBytes(size.Value)}：不到 100MB 的文件不允许走 LFS，" +
                               "也不在这里决定是否入库（那是「入库规则」的事）");
            if (decision == LfsDecision.Ignore && LfsPolicy.IsSnapshotPath(relative))
                return (false, $"{relative} 在 z-* 正式消费快照里，必须入库；超限只能选 LFS 指针");
        }

        await _writeGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var attributes = await ReadAsync(root, AttributesFile, cancellation);
            var ignore = await ReadAsync(root, IgnoreFile, cancellation);
            var lfsPaths = ToSet(LfsPolicy.ReadLfsPaths(attributes));
            var ignorePaths = ToSet(LfsPolicy.ReadIgnoredPaths(ignore));
            lfsPaths.Remove(relative);
            ignorePaths.Remove(relative);
            if (decision == LfsDecision.Lfs) lfsPaths.Add(relative);
            if (decision == LfsDecision.Ignore) ignorePaths.Add(relative);

            if (decision == LfsDecision.Lfs)
            {
                var install = await GitRunner.RunAsync(root, ["lfs", "install", "--local"], cancellation)
                    .ConfigureAwait(false);
                if (!install.Success)
                    return (false, "git lfs install 失败:\n" + install.Output);
            }

            await WriteIfChangedAsync(root, AttributesFile, attributes,
                LfsPolicy.WriteLfsBlock(attributes, lfsPaths), cancellation);
            await WriteIfChangedAsync(root, IgnoreFile, ignore,
                LfsPolicy.WriteIgnoreBlock(ignore, ignorePaths), cancellation);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, $"写入托管块失败: {ex.Message}");
        }
        finally
        {
            _writeGate.Release();
        }

        return (true, decision switch
        {
            LfsDecision.Lfs => $"已记住：{relative} → LFS 指针。下次提交生效，历史不变",
            LfsDecision.Ignore => $"已记住：{relative} → 不再纳入 git。下次提交把它移出索引，本地文件保留，历史不变",
            _ => $"已清除 {relative} 的决定；下次提交若仍 ≥100MB 会重新弹窗确认",
        });
    }

    /// <summary>提交链路用：按精确路径把一批文件记为走 LFS（弹窗里点了「是」）。</summary>
    public async Task<(bool Success, string Message)> RecordLfsAsync(
        string root, IEnumerable<string> paths, CancellationToken cancellation = default)
    {
        var install = await GitRunner.RunAsync(root, ["lfs", "install", "--local"], cancellation)
            .ConfigureAwait(false);
        if (!install.Success)
            return (false, "git lfs install 失败:\n" + install.Output);
        await _writeGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var attributes = await ReadAsync(root, AttributesFile, cancellation);
            var set = ToSet(LfsPolicy.ReadLfsPaths(attributes));
            foreach (var path in paths)
                set.Add(LfsPolicy.Normalize(path));
            await WriteIfChangedAsync(root, AttributesFile, attributes,
                LfsPolicy.WriteLfsBlock(attributes, set), cancellation);
            return (true, "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, $"写入 .gitattributes 失败: {ex.Message}");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<(HashSet<string> Lfs, HashSet<string> Ignore)> ReadDecisionsAsync(
        string root, CancellationToken cancellation = default)
        => (ToSet(LfsPolicy.ReadLfsPaths(await ReadAsync(root, AttributesFile, cancellation))),
            ToSet(LfsPolicy.ReadIgnoredPaths(await ReadAsync(root, IgnoreFile, cancellation))));

    // ---------------------------------------------------------------- 提交链路

    /// <summary>
    /// 提交前的门：托管块之外有 LFS 规则，或托管块里列着一个本机有实体、却不到 100MB 的文件，
    /// 就拒绝提交并指向修复命令。本机只有指针的文件（实体缺失）是唯一例外——
    /// 它转不回普通文件，否则会把指针文本当内容提交。
    /// </summary>
    public async Task<(bool Success, string Message)> CheckPolicyAsync(
        string root, CancellationToken cancellation = default)
    {
        var foreign = await CountForeignRulesAsync(root, cancellation).ConfigureAwait(false);
        var small = new List<string>();
        foreach (var path in LfsPolicy.ReadLfsPaths(await ReadAsync(root, AttributesFile, cancellation)))
        {
            var full = FullPath(root, path);
            if (!File.Exists(full) || WorktreeLfsHelper.IsLfsPointerFile(full))
                continue;
            if (new FileInfo(full).Length < LfsPolicy.ThresholdBytes)
                small.Add(path);
        }

        if (foreign == 0 && small.Count == 0)
            return (true, "");
        var message = new StringBuilder("本仓 LFS 规则不合规（不到 100MB 的文件一律不走 LFS）：");
        if (foreign > 0)
            message.Append($"\n  lfs 托管块之外还有 {foreign} 条 filter=lfs 规则");
        if (small.Count > 0)
            message.Append($"\n  托管块里有 {small.Count} 个文件已不足 100MB，例如 {small[0]}");
        message.Append("\n先运行 janus.gitrule.lfsrepair 修复，再提交。");
        return (false, message.ToString());
    }

    /// <summary>
    /// 在 <c>git add</c> 之前把记住的决定落进索引：
    /// 「不纳入 git」的文件若还在索引里就 <c>git rm --cached</c>（本地文件保留）；
    /// 「LFS 指针」的文件若在索引里还是普通 blob 就 <c>git add --renormalize</c>，
    /// 让它经过 LFS 过滤器重新入库。只影响下一次提交，历史不改写。
    /// </summary>
    public async Task<(bool Success, string Message)> StageDecisionsAsync(
        string root, IProgress<string>? progress, CancellationToken cancellation = default)
    {
        var (lfs, ignore) = await ReadDecisionsAsync(root, cancellation).ConfigureAwait(false);
        foreach (var path in ignore)
        {
            if (!await IsTrackedAsync(root, path, cancellation).ConfigureAwait(false))
                continue;
            var removed = await GitRunner.RunAsync(root,
                ["rm", "--cached", "--quiet", "--", Literal(path)], cancellation).ConfigureAwait(false);
            if (!removed.Success)
                return (false, $"移出索引失败 {path}:\n{removed.Output}");
            progress?.Report($"   [不纳入 git] {path} 已移出索引，本地文件保留");
        }

        var renormalize = new List<string>();
        foreach (var path in lfs)
        {
            if (await IsTrackedAsync(root, path, cancellation).ConfigureAwait(false)
                && !await IndexHoldsPointerAsync(root, path, cancellation).ConfigureAwait(false))
                renormalize.Add(path);
        }
        if (renormalize.Count > 0)
        {
            var added = await RenormalizeAsync(root, renormalize, cancellation).ConfigureAwait(false);
            if (!added.Success)
                return (false, $"转为 LFS 指针失败:\n{added.Output}");
            foreach (var path in renormalize)
                progress?.Report($"   [LFS] {path} 转为指针入库");
        }
        return (true, "");
    }

    // ---------------------------------------------------------------- 修复

    /// <summary>
    /// 一个仓的修复：
    /// 1. 所有 .gitattributes 里托管块之外的 LFS 属性去掉（保留 -text）；
    /// 2. ≥100MB 的指针按精确路径补进 lfs 托管块，继续走 LFS；
    /// 3. 不足 100MB 的指针转回普通入库；本机缺实体的先 <c>git lfs pull</c>，取不到的暂留指针并点名；
    /// 4. <paramref name="commit"/> 为真时本地提交，按 300MB 分批，每批一个提交，便于之后分批推送。
    ///
    /// <paramref name="formatOnly"/> 为真时只删**按格式**的规则（<c>*.dll</c> 这类带通配的），
    /// 精确路径的规则原样保留。
    ///
    /// 哪些文件要转回，一律在改写之后问 <c>git check-attr</c>：规则删掉后不再被任何 LFS 规则覆盖、
    /// 而索引里还是指针的文件，下一次克隆拿到的就是一段指针文本——这种文件必须同批转回。
    /// 由 git 自己判定覆盖关系，不在这里重新实现 gitattributes 的匹配。
    ///
    /// 不改写历史、不推送。暂存区里已有别的改动时拒绝，免得把别人的东西一起提交。
    /// </summary>
    public async Task<(bool Success, string Message, LfsRepairReport? Report)> RepairAsync(
        string project, bool commit, bool dryRun, IProgress<string>? progress,
        CancellationToken cancellation = default, bool formatOnly = false)
    {
        var resolved = await _projects.ResolveWorktreeAsync(project).ConfigureAwait(false);
        if (!resolved.Success || resolved.Worktree == null)
            return (false, resolved.Message, null);
        var name = resolved.Worktree.BranchName;
        var root = resolved.Worktree.WorktreePath;

        if (!await WorktreeLfsHelper.IsGitLfsAvailableAsync(root).ConfigureAwait(false))
            return (false, $"{name}: 本机未安装 git-lfs，无法判断哪些文件是指针", null);

        var attributeFiles = await ListAttributeFilesAsync(root, cancellation).ConfigureAwait(false);
        if (!dryRun)
        {
            var staged = await GitRunner.RunAsync(root, ["diff", "--cached", "--quiet"], cancellation)
                .ConfigureAwait(false);
            if (staged.ExitCode != 0)
                return (false, $"{name}: 暂存区已有改动，先提交或撤回再修复", null);
            var dirty = await GitRunner.RunAsync(root,
                ["status", "--porcelain", "--", .. attributeFiles.Select(Literal)], cancellation).ConfigureAwait(false);
            if (dirty.Success && dirty.Output.Trim().Length > 0)
                return (false, $"{name}: .gitattributes 有未提交改动，先处理:\n{dirty.Output}", null);
        }

        var pointers = await ListPointersAsync(root, cancellation).ConfigureAwait(false);
        if (pointers == null)
            return (false, $"{name}: 读取 LFS 清单失败", null);

        // 「缺实体」只认工作区里确实是指针文本的文件。lfs 也把「本地改过的真实文件」报成未检出，
        // 那种文件下次提交自然以真实内容入库，不能被钉成 LFS 例外（否则提交前门会拦它）。
        bool PointerOnDisk(LfsPointer p) => WorktreeLfsHelper.IsLfsPointerFile(FullPath(root, p.Path));
        var smallMissing = pointers.Where(p => p.Size < LfsPolicy.ThresholdBytes && !p.Checkout && PointerOnDisk(p))
            .Select(p => p.Path).ToList();
        if (!dryRun && smallMissing.Count > 0)
        {
            progress?.Report($"[{name}] {smallMissing.Count} 个小文件本机缺实体，尝试 git lfs pull...");
            foreach (var path in smallMissing)
                await GitRunner.RunAsync(root, ["lfs", "pull", "--include", LfsIncludePattern(path)],
                    cancellation).ConfigureAwait(false);
            pointers = await ListPointersAsync(root, cancellation).ConfigureAwait(false) ?? pointers;
        }

        var large = pointers.Where(p => p.Size >= LfsPolicy.ThresholdBytes).Select(p => p.Path).ToList();
        var missing = pointers.Where(p => p.Size < LfsPolicy.ThresholdBytes && !p.Checkout && PointerOnDisk(p))
            .Select(p => p.Path).ToList();

        // 托管块：原有条目里仍然 ≥100MB（或还没提交、只在工作区里）的留下，加上现存的大指针与缺实体的例外。
        var attributesText = await ReadAsync(root, AttributesFile, cancellation);
        var keep = ToSet(large.Concat(missing));
        foreach (var path in LfsPolicy.ReadLfsPaths(attributesText))
        {
            var full = FullPath(root, path);
            if (File.Exists(full) && (WorktreeLfsHelper.IsLfsPointerFile(full)
                                      || new FileInfo(full).Length >= LfsPolicy.ThresholdBytes))
                keep.Add(path);
        }

        var stripped = 0;
        var rewrites = new Dictionary<string, (string Before, string After)>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in attributeFiles)
        {
            var before = await ReadAsync(root, file, cancellation);
            var after = LfsPolicy.StripForeignLfsRules(before, out var count, formatOnly);
            if (file.Equals(AttributesFile, StringComparison.OrdinalIgnoreCase))
                after = LfsPolicy.WriteLfsBlock(after, keep);
            stripped += count;
            if (after != before)
                rewrites[file] = (before, after);
        }
        if (!rewrites.ContainsKey(AttributesFile) && keep.Count > 0)
        {
            var after = LfsPolicy.WriteLfsBlock(attributesText, keep);
            if (after != attributesText)
                rewrites[AttributesFile] = (attributesText, after);
        }

        // 先把改写落盘，再让 git 说哪些小指针已不被任何 LFS 规则覆盖。预演也这样做，结束后原样写回。
        List<LfsPointer> convert;
        try
        {
            foreach (var (file, (_, after)) in rewrites)
                await File.WriteAllTextAsync(FullPath(root, file), after, new UTF8Encoding(false), cancellation);
            var covered = await LfsCoveredAsync(root,
                pointers.Where(p => p.Size < LfsPolicy.ThresholdBytes && p.Checkout).Select(p => p.Path).ToList(),
                cancellation).ConfigureAwait(false);
            if (covered == null)
                return (false, $"{name}: git check-attr 失败，无法判断哪些文件失去 LFS 规则", null);
            convert = pointers.Where(p => p.Size < LfsPolicy.ThresholdBytes && p.Checkout
                                          && !covered.Contains(p.Path)).ToList();
        }
        finally
        {
            if (dryRun)
                foreach (var (file, (before, _)) in rewrites)
                    await File.WriteAllTextAsync(FullPath(root, file), before, new UTF8Encoding(false), CancellationToken.None);
        }

        var what = formatOnly ? "按格式的 LFS 规则" : "托管块外 LFS 规则";
        var convertedBytes = convert.Sum(p => p.Size);
        var batches = Batch(convert, ProjectService.PushChunkBudgetBytes);
        var plan = $"{name}: 去掉{what} {stripped} 条；{convert.Count} 个不足 100MB 的文件转回普通入库" +
                   $"（{LfsPolicy.FormatBytes(convertedBytes)}，{Math.Max(batches.Count, rewrites.Count > 0 ? 1 : 0)} 批）；" +
                   $"≥100MB 继续走 LFS {large.Count} 个；本机缺实体暂留指针 {missing.Count} 个";

        if (dryRun || (rewrites.Count == 0 && convert.Count == 0))
            return (true, dryRun ? "[预演] " + plan : $"{name}: 已合规，无需修复",
                new LfsRepairReport(name, dryRun, stripped, dryRun ? convert.Count : 0, dryRun ? convertedBytes : 0,
                    large.Count, missing, 0, plan));

        var addAttributes = await GitRunner.RunAsync(root,
            ["add", "--", .. rewrites.Keys.Select(Literal)], cancellation).ConfigureAwait(false);
        if (!addAttributes.Success)
            return (false, $"{name}: 暂存 .gitattributes 失败:\n{addAttributes.Output}", null);

        var commits = 0;
        var total = Math.Max(batches.Count, 1);
        for (var i = 0; i < total; i++)
        {
            var batch = i < batches.Count ? batches[i] : [];
            if (batch.Count > 0)
            {
                var added = await RenormalizeAsync(root, batch.Select(p => p.Path).ToList(), cancellation)
                    .ConfigureAwait(false);
                if (!added.Success)
                    return (false, $"{name}: 第 {i + 1} 批转回普通入库失败:\n{added.Output}", null);
            }
            progress?.Report($"[{name}] 第 {i + 1}/{total} 批：{batch.Count} 个文件，{LfsPolicy.FormatBytes(batch.Sum(p => p.Size))}");
            if (!commit)
                continue;
            var message = formatOnly
                ? $"LFS 修复：删除按格式的 LFS 规则，{batch.Count} 个不足 100MB 的文件转回普通入库（第 {i + 1}/{total} 批）"
                : $"LFS 修复：{batch.Count} 个不足 100MB 的文件转回普通入库（第 {i + 1}/{total} 批）";
            var committed = await GitRunner.RunAsync(root, ["commit", "-m", message], cancellation)
                .ConfigureAwait(false);
            if (!committed.Success)
                return (false, $"{name}: 第 {i + 1} 批提交失败:\n{committed.Output}", null);
            commits++;
        }

        // 复查：索引里还是指针、却已不被任何 LFS 规则覆盖的小文件必须为零。
        // 只删格式规则时，精确路径覆盖的小指针按设计还留着，不算失败。
        var after2 = await ListPointersAsync(root, cancellation).ConfigureAwait(false) ?? [];
        var smallLeft = after2.Where(p => p.Size < LfsPolicy.ThresholdBytes && p.Checkout).Select(p => p.Path).ToList();
        var stillCovered = await LfsCoveredAsync(root, smallLeft, cancellation).ConfigureAwait(false) ?? [];
        var left = smallLeft.Count(path => !stillCovered.Contains(path));
        var detail = plan + (commit ? $"；已本地提交 {commits} 次，未推送" : "；已暂存，未提交") +
                     (left > 0 ? $"；⚠ 仍有 {left} 个小文件是指针却已没有 LFS 规则" : "");
        return (left == 0, detail,
            new LfsRepairReport(name, false, stripped, convert.Count, convertedBytes, large.Count, missing, commits, detail));
    }

    /// <summary>
    /// 这些路径里哪些仍被 LFS 规则覆盖（<c>git check-attr filter</c> 为 lfs）。
    /// 路径按参数分批传，不走标准输入——标准输入的代码页不是 UTF-8，中文路径会坏。
    /// </summary>
    private static async Task<HashSet<string>?> LfsCoveredAsync(
        string root, IReadOnlyList<string> paths, CancellationToken cancellation)
    {
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < paths.Count; i += 100)
        {
            var chunk = paths.Skip(i).Take(100).ToList();
            var result = await GitRunner.RunAsync(root,
                ["check-attr", "-z", "filter", "--", .. chunk], cancellation).ConfigureAwait(false);
            if (!result.Success)
                return null;
            var parts = result.Output.Split('\0');
            for (var j = 0; j + 2 < parts.Length; j += 3)
                if (parts[j + 2] == "lfs")
                    covered.Add(parts[j].Trim('\n', '\r'));
        }
        return covered;
    }

    private static List<List<LfsPointer>> Batch(IEnumerable<LfsPointer> files, long budget)
    {
        var batches = new List<List<LfsPointer>>();
        var current = new List<LfsPointer>();
        long bytes = 0;
        foreach (var file in files.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            if (current.Count > 0 && bytes + file.Size > budget)
            {
                batches.Add(current);
                current = [];
                bytes = 0;
            }
            current.Add(file);
            bytes += file.Size;
        }
        if (current.Count > 0)
            batches.Add(current);
        return batches;
    }

    // ---------------------------------------------------------------- git 细节

    private sealed record LfsPointer(string Path, long Size, bool Checkout);

    /// <summary><c>git lfs ls-files --json</c>：路径、实体大小、工作区里是不是实体。</summary>
    private static async Task<List<LfsPointer>?> ListPointersAsync(string root, CancellationToken cancellation)
    {
        var listed = await GitRunner.RunAsync(root, ["lfs", "ls-files", "--json"], cancellation)
            .ConfigureAwait(false);
        if (!listed.Success)
            return null;
        var start = listed.Output.IndexOf('{');
        if (start < 0)
            return [];
        try
        {
            using var document = JsonDocument.Parse(listed.Output[start..]);
            if (!document.RootElement.TryGetProperty("files", out var files)
                || files.ValueKind != JsonValueKind.Array)
                return [];
            return files.EnumerateArray().Select(file => new LfsPointer(
                    file.GetProperty("name").GetString() ?? "",
                    file.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
                    !file.TryGetProperty("checkout", out var checkout) || checkout.GetBoolean()))
                .Where(file => file.Path.Length > 0)
                .ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>根目录与所有被跟踪的嵌套 .gitattributes。</summary>
    private static async Task<List<string>> ListAttributeFilesAsync(string root, CancellationToken cancellation)
    {
        var files = new List<string> { AttributesFile };
        var listed = await GitRunner.RunAsync(root,
            ["ls-files", "-z", "--", ":(glob)**/.gitattributes"], cancellation).ConfigureAwait(false);
        if (listed.Success)
            files.AddRange(listed.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Select(path => path.Trim('\n', '\r'))
                .Where(path => path.Length > 0 && !path.Equals(AttributesFile, StringComparison.OrdinalIgnoreCase)));
        return files;
    }

    private static async Task<int> CountForeignRulesAsync(string root, CancellationToken cancellation)
    {
        var total = 0;
        foreach (var file in await ListAttributeFilesAsync(root, cancellation).ConfigureAwait(false))
            total += LfsPolicy.CountForeignLfsRules(await ReadAsync(root, file, cancellation));
        return total;
    }

    private static async Task<bool> IsTrackedAsync(string root, string path, CancellationToken cancellation)
    {
        var result = await GitRunner.RunAsync(root, ["ls-files", "--", Literal(path)], cancellation)
            .ConfigureAwait(false);
        return result.Success && result.Output.Trim().Length > 0;
    }

    private static async Task<bool> IsIgnoredAsync(string root, string path, CancellationToken cancellation)
    {
        var result = await GitRunner.RunAsync(root, ["check-ignore", "-q", "--", path], cancellation)
            .ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    /// <summary>索引里这一项是不是 LFS 指针文本。</summary>
    private static async Task<bool> IndexHoldsPointerAsync(string root, string path, CancellationToken cancellation)
    {
        var size = await GitRunner.RunAsync(root, ["cat-file", "-s", ":" + path], cancellation)
            .ConfigureAwait(false);
        if (!size.Success || !long.TryParse(size.Output.Trim(), out var bytes) || bytes > 2048)
            return false;
        var blob = await GitRunner.RunAsync(root, ["cat-file", "blob", ":" + path], cancellation)
            .ConfigureAwait(false);
        return blob.Success && blob.Output.StartsWith("version https://git-lfs.github.com/spec/v1", StringComparison.Ordinal);
    }

    /// <summary>
    /// 按精确路径 <c>git add --renormalize</c>。路径多、又常是中文，走 NUL 分隔的 UTF-8
    /// pathspec 文件，既不撞命令行长度，也不受标准输入代码页影响。
    /// </summary>
    private static async Task<GitResult> RenormalizeAsync(
        string root, IReadOnlyList<string> paths, CancellationToken cancellation)
    {
        var list = Path.Combine(Path.GetTempPath(), $"janus-pathspec-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(list,
                string.Join('\0', paths.Select(Literal)) + "\0", new UTF8Encoding(false), cancellation);
            return await GitRunner.RunAsync(root,
                ["add", "--renormalize", "--pathspec-from-file=" + list, "--pathspec-file-nul"],
                cancellation).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(list); } catch (IOException) { }
        }
    }

    private static async Task<long?> SizeOfAsync(string root, string path, CancellationToken cancellation)
    {
        var full = FullPath(root, path);
        if (!File.Exists(full))
            return null;
        if (!WorktreeLfsHelper.IsLfsPointerFile(full))
            return new FileInfo(full).Length;
        // 工作区里只有指针：大小以指针里记的实体大小为准。
        var text = await File.ReadAllTextAsync(full, cancellation);
        var line = text.Split('\n').FirstOrDefault(l => l.StartsWith("size ", StringComparison.Ordinal));
        return line != null && long.TryParse(line[5..].Trim(), out var size) ? size : 0;
    }

    /// <summary>
    /// <c>git lfs pull --include</c> 吃的是 gitignore 风格、逗号分隔的模式：
    /// 转义通配与逗号。带目录的路径本来就从根匹配；根目录下的裸文件名可能多拉几个同名文件，无害。
    /// </summary>
    private static string LfsIncludePattern(string path)
        => LfsPolicy.IgnoreLine(path)[1..].Replace(",", "\\,", StringComparison.Ordinal);

    private static string Literal(string path) => ":(literal)" + path;

    private static string FullPath(string root, string relative)
        => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static HashSet<string> ToSet(IEnumerable<string> paths)
        => new(paths.Select(LfsPolicy.Normalize), StringComparer.OrdinalIgnoreCase);

    private static async Task<string> ReadAsync(string root, string relative, CancellationToken cancellation)
    {
        var full = FullPath(root, relative);
        return File.Exists(full) ? await File.ReadAllTextAsync(full, cancellation) : string.Empty;
    }

    private static async Task WriteIfChangedAsync(
        string root, string relative, string before, string after, CancellationToken cancellation)
    {
        if (before == after)
            return;
        await File.WriteAllTextAsync(FullPath(root, relative), after, new UTF8Encoding(false), cancellation);
    }
}
