using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Services;

namespace HistoryJanus.Git;

/// <summary>
/// 文件格式全覆盖扫描。三条效率约束：
/// ①按格式聚合不按文件展开(实测 39311 文件 → 278 格式,141:1);
/// ②项目级有界并行;③缓存 + 增量失效(HEAD + 索引 mtime + 两规则文件哈希)。
/// git 自身的 ls-files 即权威归宿判定,不重复实现忽略匹配。
/// </summary>
public sealed class FormatInventoryService
{
    /// <summary>无扩展名文件的台账键。</summary>
    public const string NoExtension = "(无扩展名)";

    /// <summary>目录候选的归并深度:实测 2 段即可把 Unity Library 4314 个文件收敛成一行。</summary>
    private const int DirectoryRollupSegments = 2;

    /// <summary>
    /// 目录候选阈值：文件数达标才单列为目录候选，
    /// 未达标的散尾统一归入 other 类别整体决策,避免长尾淹没缺口清单。
    /// </summary>
    private const int DirectoryCandidateThreshold = 50;

    private readonly ProjectService _projects;
    private readonly IShellLog _log;
    private readonly string _cachePath;
    private readonly SemaphoreSlim _scanGate = new(1, 1);

    private Dictionary<string, ProjectScan> _cache = new(StringComparer.OrdinalIgnoreCase);
    private bool _cacheLoaded;

    public FormatInventoryService(ProjectService projects, IShellLog log, string dataDir)
    {
        _projects = projects;
        _log = log;
        _cachePath = Path.Combine(AppPaths.GetDataDir(dataDir), "format-inventory.json");
    }

    /// <summary>
    /// 扫描并产出台账。project 为空 = 全库;depth ∈ quick/normal/deep;
    /// refresh=true 忽略缓存全量重扫。
    /// </summary>
    public async Task<(bool Success, string Message, InventoryReport? Report)> ScanAsync(
        string? project,
        bool deep,
        bool refresh,
        IProgress<string>? progress,
        CancellationToken cancellation = default)
    {
        var depth = deep ? "deep" : "default";
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var targets = await ResolveTargetsAsync(project).ConfigureAwait(false);
        if (targets.Count == 0)
            return (false, string.IsNullOrWhiteSpace(project) ? "未发现任何工作树" : $"未找到项目: {project}", null);

        await _scanGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            LoadCache();

            var scans = new ProjectScan?[targets.Count];
            var cachedCount = 0;
            var done = 0;
            using var limiter = new SemaphoreSlim(Math.Min(Environment.ProcessorCount, 8));

            var tasks = targets.Select(async (target, index) =>
            {
                await limiter.WaitAsync(cancellation).ConfigureAwait(false);
                try
                {
                    var key = await BuildCacheKeyAsync(target, depth, cancellation).ConfigureAwait(false);
                    if (!refresh && key != null
                        && _cache.TryGetValue(target.BranchName, out var cached)
                        && cached.Key == key)
                    {
                        scans[index] = cached;
                        Interlocked.Increment(ref cachedCount);
                    }
                    else
                    {
                        scans[index] = await ScanProjectAsync(target, depth, key, cancellation)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.Warn("rule", $"扫描 {target.BranchName} 失败,已跳过: {ex.Message}");
                }
                finally
                {
                    limiter.Release();
                    var completed = Interlocked.Increment(ref done);
                    if (targets.Count > 1 && completed % 5 == 0)
                        progress?.Report($"已扫描 {completed}/{targets.Count} 个项目");
                }
            }).ToList();

            await Task.WhenAll(tasks).ConfigureAwait(false);

            var valid = scans.Where(s => s != null).Select(s => s!).ToList();
            if (valid.Count == 0)
                return (false, "全部项目扫描失败,详见日志", null);

            foreach (var scan in valid)
                _cache[scan.Project] = scan;
            SaveCache();

            watch.Stop();
            var report = Aggregate(valid, depth, cachedCount, watch.ElapsedMilliseconds);
            return (true, Render(report, project), report);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    /// <summary>一次扫描同时生成未决清单与规则建议；未知格式留给人工判断。</summary>
    public async Task<(bool Success, string Message, GitRuleReviewReport? Report)> ReviewAsync(
        string? project,
        long lfsThresholdBytes,
        IProgress<string>? progress,
        CancellationToken cancellation = default)
    {
        var (success, message, report) = await ScanAsync(project, deep: false, refresh: false, progress, cancellation)
            .ConfigureAwait(false);
        if (!success || report == null)
            return (false, message, null);

        var gapFormats = report.Formats.Where(row => row.UndecidedCount > 0)
            .OrderByDescending(row => row.UndecidedCount).ToList();
        var gapDirectories = report.Directories.Where(row => !row.Ignored)
            .OrderByDescending(row => row.FileCount).ToList();
        var gaps = report with { Formats = gapFormats, Directories = gapDirectories };

        var suggestions = new List<RuleSuggestion>();
        var unknown = new List<FormatRow>();
        foreach (var row in gapFormats)
        {
            var suggestion = RuleSuggestionEngine.Suggest(row, lfsThresholdBytes);
            if (suggestion != null)
                suggestions.Add(suggestion);
            else
                unknown.Add(row);
        }

        suggestions = suggestions.OrderByDescending(s => s.AffectedFiles).ToList();

        var covered = suggestions.Sum(suggestion => suggestion.AffectedFiles);
        var suggestedCoverageRate = report.UndecidedCount == 0
            ? 1d
            : (double)covered / report.UndecidedCount;
        var review = new GitRuleReviewReport(
            gaps, suggestions, unknown, covered, suggestedCoverageRate);
        var text = new StringBuilder();
        text.Append($"未决与建议({(string.IsNullOrWhiteSpace(project) ? "全库" : project)}): " +
                    $"覆盖率 {report.CoverageRate:P1},未决 {report.UndecidedCount} 个文件;" +
                    $"{suggestions.Count} 条建议可覆盖 {covered} 个({suggestedCoverageRate:P1});" +
                    $"{unknown.Count} 种未知格式待人工判定");
        text.Append($"\n未决清单: {gapFormats.Count} 种格式 / {gapDirectories.Count} 个目录候选");
        if (gapFormats.Count == 0 && gapDirectories.Count == 0)
            text.Append("\n  无未决项");
        foreach (var row in gapFormats.Take(30))
            text.Append($"\n  {row.Format,-16} 未决 {row.UndecidedCount,6}  (共 {row.FileCount} 个 / {row.ProjectCount} 个项目)");
        if (gapFormats.Count > 30)
            text.Append($"\n  ... 另有 {gapFormats.Count - 30} 种格式,完整台账见 janus.gitrule.scan");
        foreach (var row in gapDirectories.Take(15))
            text.Append($"\n  [目录] {row.Project}/{row.Directory}  {row.FileCount} 个无扩展名文件");
        if (gapDirectories.Count > 15)
            text.Append($"\n  ... 另有 {gapDirectories.Count - 15} 个目录候选");

        text.Append("\n规则建议:\n格式                建议            影响文件  依据");

        foreach (var s in suggestions.Take(40))
        {
            var action = !s.Track ? "忽略" : s.Lfs ? "Git+LFS" : s.Lf ? "Git+LF" : "Git";
            text.Append($"\n  {s.Pattern,-16} {action,-14} {s.AffectedFiles,7}  {s.Reason}");
        }

        if (suggestions.Count > 40)
            text.Append($"\n  ... 另有 {suggestions.Count - 40} 条建议(Data 载荷含全量)");

        if (unknown.Count > 0)
        {
            text.Append($"\n未知格式(不建议,需人工判定)Top: " +
                        string.Join(" ", unknown.Take(12).Select(u => $"{u.Format}({u.UndecidedCount})")));
        }

        text.Append("\n应用: janus.gitrule.set 逐条采纳;目录候选用 janus.gitrule.set pattern=<目录>/ track=false");
        return (true, text.ToString(), review);
    }

    // ---------------------------------------------------------------- 单项目扫描

    private async Task<ProjectScan> ScanProjectAsync(
        WorktreeInfo target, string depth, string? key, CancellationToken cancellation)
    {
        var root = target.WorktreePath;

        // §4.1:三态枚举由 git 给出权威答案,不自行实现忽略匹配
        var trackedTask = GitRunner.RunAsync(root, ["ls-files", "-z"], cancellation: cancellation);
        var othersTask = GitRunner.RunAsync(
            root, ["ls-files", "-z", "--others", "--exclude-standard"], cancellation: cancellation);
        var ignoredTask = GitRunner.RunAsync(
            root, ["ls-files", "-z", "--others", "--ignored", "--exclude-standard"], cancellation: cancellation);
        await Task.WhenAll(trackedTask, othersTask, ignoredTask).ConfigureAwait(false);

        var tracked = SplitPaths((await trackedTask).Output);
        var others = SplitPaths((await othersTask).Output);
        var ignored = SplitPaths((await ignoredTask).Output);

        var declared = await GitFileRuleService.ReadDeclaredRulesAsync(root, cancellation).ConfigureAwait(false);

        var formats = new Dictionary<string, FormatStat>(StringComparer.OrdinalIgnoreCase);
        var dirs = new Dictionary<string, DirectoryStat>(StringComparer.OrdinalIgnoreCase);

        // 体积统计恒开:逐文件 stat 求准确最大/合计(驱动 LFS 建议;全库仅多耗 580ms)
        const bool measure = true;

        Accumulate(tracked, FileKind.Tracked);
        Accumulate(others, FileKind.Untracked);
        Accumulate(ignored, FileKind.Ignored);

        // 内容嗅探:每格式一次(O(格式) 而非 O(文件)),纠正扩展名清单的误判
        foreach (var (_, stat) in formats)
        {
            if (stat.Sample.Length > 0)
                stat.BinarySniff = SniffBinary(root, stat.Sample);
        }

        // deep 档:LFS 指针核验(§4.1 唯一昂贵项 725ms/项目,故 opt-in)
        var lfsPointers = 0;
        if (depth == "deep")
        {
            var lfs = await GitRunner.RunAsync(root, ["lfs", "ls-files"], cancellation: cancellation)
                .ConfigureAwait(false);
            if (lfs.Success)
                lfsPointers = lfs.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        }

        return new ProjectScan(
            target.BranchName, key ?? "", depth,
            tracked.Count, ignored.Count, others.Count, lfsPointers,
            formats, dirs,
            declared.ToDictionary(
                kv => kv.Key,
                kv => new DeclaredDto(kv.Value.Track, kv.Value.Lfs, kv.Value.Lf),
                StringComparer.OrdinalIgnoreCase));

        void Accumulate(IReadOnlyList<string> paths, FileKind kind)
        {
            foreach (var path in paths)
            {
                var format = FormatOf(path);
                if (!formats.TryGetValue(format, out var stat))
                    formats[format] = stat = new FormatStat { Sample = path };

                switch (kind)
                {
                    case FileKind.Tracked: stat.Tracked++; break;
                    case FileKind.Ignored: stat.Ignored++; break;
                    default: stat.Untracked++; break;
                }

                if (measure)
                {
                    try
                    {
                        var info = new FileInfo(
                            Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
                        if (info.Exists)
                        {
                            stat.MaxBytes = Math.Max(stat.MaxBytes, info.Length);
                            stat.TotalBytes += info.Length;
                            if (info.Length > stat.LargestBytes)
                            {
                                stat.LargestBytes = info.Length;
                                stat.Sample = path; // 代表路径取该格式最大的文件,便于人工核查
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // 单个文件不可读不影响整体统计
                    }
                }

                if (format != NoExtension)
                    continue;

                // 无扩展名文件按目录归并(§3.3),供目录规则决策
                var directory = RollupDirectory(path);
                if (!dirs.TryGetValue(directory, out var directoryStat))
                    dirs[directory] = directoryStat = new DirectoryStat();
                switch (kind)
                {
                    case FileKind.Tracked: directoryStat.Tracked++; break;
                    case FileKind.Ignored: directoryStat.Ignored++; break;
                    default: directoryStat.Untracked++; break;
                }
            }
        }
    }

    // ---------------------------------------------------------------- 聚合与渲染

    private static InventoryReport Aggregate(
        IReadOnlyList<ProjectScan> scans, string depth, int cachedCount, long elapsedMs)
    {
        var merged = new Dictionary<string, MergedFormat>(StringComparer.OrdinalIgnoreCase);

        foreach (var scan in scans)
        {
            foreach (var (format, stat) in scan.Formats)
            {
                if (!merged.TryGetValue(format, out var m))
                    merged[format] = m = new MergedFormat();

                m.Projects.Add(scan.Project);
                m.Tracked += stat.Tracked;
                m.Ignored += stat.Ignored;
                m.Untracked += stat.Untracked;
                m.MaxBytes = Math.Max(m.MaxBytes, stat.MaxBytes);
                m.TotalBytes += stat.TotalBytes;
                if (stat.BinarySniff)
                    m.BinarySniff = true; // 任一项目样本为二进制即认定该格式二进制(保守取向)

                // 声明状态:任一项目声明过即记入;各项目声明不一致标为混合
                if (scan.Declared.TryGetValue(format, out var d))
                {
                    m.DeclaredIn++;
                    m.Track ??= d.Track;
                    m.Lfs ??= d.Lfs;
                    m.Lf ??= d.Lf;
                    if (m.Track != d.Track || m.Lfs != d.Lfs || m.Lf != d.Lf)
                        m.Divergent = true;
                }
            }
        }

        var formats = merged.Select(kv =>
        {
            var m = kv.Value;
            var projectCount = m.Projects.Count;
            // 无扩展名没有可声明的格式规则：已跟踪文件已经有明确归宿，只把未跟踪文件计为未决。
            var undecided = m.Untracked
                            + (kv.Key != NoExtension && m.DeclaredIn == 0 ? m.Tracked : 0);
            var state = m.DeclaredIn == 0
                ? (m.Ignored > 0 && m.Tracked == 0 ? "已忽略(无显式规则)" : "未决")
                : m.Divergent
                    ? $"混合({m.DeclaredIn}/{projectCount} 项目声明)"
                    : m.Track == false ? "已忽略管" : "已跟踪管";

            return new FormatRow(
                kv.Key, m.Tracked + m.Ignored + m.Untracked, projectCount,
                m.Tracked, m.Ignored, undecided,
                m.MaxBytes, m.TotalBytes, state, m.Track, m.Lfs, m.Lf,
                Suggestion: "", BinarySniff: m.BinarySniff);
        })
        .OrderByDescending(f => f.UndecidedCount)
        .ThenByDescending(f => f.FileCount)
        .ToList();

        // 达阈值的目录单列为候选；散尾并入 other 一行整体决策。
        var allDirs = scans.SelectMany(scan => scan.Directories
            .Where(kv => kv.Value.Untracked > 0)
            .Select(kv => new DirectoryCandidateRow(
                scan.Project, kv.Key, kv.Value.Untracked, Ignored: false))).ToList();

        var dirs = allDirs.Where(d => d.FileCount >= DirectoryCandidateThreshold)
            .OrderByDescending(d => d.FileCount)
            .ToList();

        var tail = allDirs.Where(d => d.FileCount < DirectoryCandidateThreshold).ToList();
        if (tail.Count > 0)
        {
            dirs.Add(new DirectoryCandidateRow(
                $"(共 {tail.Select(t => t.Project).Distinct().Count()} 个项目)",
                $"other:散尾无扩展名文件({tail.Count} 处目录)",
                tail.Sum(t => t.FileCount),
                Ignored: tail.All(candidate => candidate.Ignored)));
        }

        var total = formats.Sum(f => (long)f.FileCount);
        var undecidedTotal = formats.Sum(f => (long)f.UndecidedCount);
        var coverage = total == 0 ? 1d : 1d - (double)undecidedTotal / total;

        return new InventoryReport(
            scans.Count, (int)total,
            scans.Sum(s => s.Tracked), scans.Sum(s => s.Ignored), (int)undecidedTotal,
            coverage, depth, cachedCount, elapsedMs, formats, dirs);
    }

    private static string Render(InventoryReport report, string? project)
    {
        var scope = string.IsNullOrWhiteSpace(project) ? "全库" : project;
        var text = new StringBuilder();
        text.Append($"格式台账({scope},{report.Depth} 档): {report.Formats.Count} 种格式 / " +
                    $"{report.FileCount} 个文件 / {report.ProjectCount} 个项目");
        text.Append($"\n覆盖率 {report.CoverageRate:P1}(未决 {report.UndecidedCount} 个)" +
                    $";跟踪 {report.TrackedCount} / 忽略 {report.IgnoredCount}");
        text.Append($"\n耗时 {report.ElapsedMs}ms(缓存命中 {report.CachedProjects}/{report.ProjectCount} 个项目)");

        var sized = report.Depth != "quick";
        text.Append(sized
            ? "\n格式                文件数   未决  项目     最大     合计  规则          状态"
            : "\n格式                文件数   未决  项目  规则          状态");

        foreach (var f in report.Formats.Take(40))
        {
            var rule = f.Track == null
                ? "-"
                : $"{(f.Track == true ? "Git" : "忽略")}{(f.Lfs == true ? "+LFS" : "")}{(f.Lf == true ? "+LF" : "")}";
            text.Append($"\n  {f.Format,-16} {f.FileCount,7} {f.UndecidedCount,6} {f.ProjectCount,5}");
            if (sized)
                text.Append($" {Size(f.MaxBytes),8} {Size(f.TotalBytes),8}");
            text.Append($"  {rule,-12}  {f.RuleState}");
        }

        if (report.Formats.Count > 40)
            text.Append($"\n  … 另有 {report.Formats.Count - 40} 种格式(Data 载荷含全量)");

        if (report.Directories.Count > 0)
        {
            text.Append($"\n目录候选(无扩展名文件,需目录规则): {report.Directories.Count} 个");
            foreach (var d in report.Directories.Take(8))
                text.Append($"\n  {d.Project}/{d.Directory}  {d.FileCount} 个");
        }

        text.Append("\njanus.gitrule.review 合并查看未决与建议;deep=true 加 LFS 指针核验");
        return text.ToString();
    }

    private static string Size(long bytes) => bytes switch
    {
        <= 0 => "-",
        < 1024 => $"{bytes}B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#}K",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#}M",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##}G",
    };

    // ---------------------------------------------------------------- 缓存(§4.3)

    /// <summary>失效键 = HEAD sha + 索引 mtime + 两规则文件哈希;任一变化即重扫该项目。</summary>
    private static async Task<string?> BuildCacheKeyAsync(
        WorktreeInfo target, string depth, CancellationToken cancellation)
    {
        try
        {
            var root = target.WorktreePath;
            if (!Directory.Exists(root))
                return null;

            var head = await GitRunner.RunAsync(root, ["rev-parse", "HEAD"], cancellation: cancellation)
                .ConfigureAwait(false);
            var headSha = head.Success ? head.Output.Trim() : "nohead";

            var indexStamp = await ReadIndexStampAsync(root, cancellation).ConfigureAwait(false);

            return $"{depth}|{headSha}|{indexStamp}|" +
                   $"{HashFile(Path.Combine(root, ".gitignore"))}|{HashFile(Path.Combine(root, ".gitattributes"))}";
        }
        catch (Exception)
        {
            return null; // 键构建失败 → 视为无缓存,照常重扫
        }
    }

    /// <summary>
    /// 读取索引文件的时间戳，作为「HEAD 没动但暂存区动了」的缓存失效信号。
    /// </summary>
    /// <remarks>
    /// 两种仓形态都要支持：独立仓的 <c>.git</c> 是目录，索引就在其中；
    /// git worktree 的 <c>.git</c> 是文件，内含真实 gitdir 路径。
    /// 一项目一仓（DEC-015）之后绝大多数项目是前者，而这里原本只处理后者，
    /// 于是 indexStamp 恒为 noindex——键再也感知不到暂存区变化，暂存但未提交时会命中过期缓存。
    /// </remarks>
    internal static async Task<string> ReadIndexStampAsync(string root, CancellationToken cancellation)
    {
        const string missing = "noindex";
        var gitPath = Path.Combine(root, ".git");

        string gitDir;
        if (Directory.Exists(gitPath))
        {
            gitDir = gitPath;
        }
        else if (File.Exists(gitPath))
        {
            var line = await File.ReadAllTextAsync(gitPath, cancellation).ConfigureAwait(false);
            gitDir = line.Replace("gitdir:", "").Trim();
            if (gitDir.Length == 0)
                return missing;
            if (!Path.IsPathRooted(gitDir))
                gitDir = Path.GetFullPath(Path.Combine(root, gitDir));
        }
        else
        {
            return missing;
        }

        var index = Path.Combine(gitDir, "index");
        return File.Exists(index)
            ? File.GetLastWriteTimeUtc(index).Ticks.ToString(CultureInfo.InvariantCulture)
            : missing;
    }

    private static string HashFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return "none";
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream))[..8];
        }
        catch (Exception)
        {
            return "err";
        }
    }

    private void LoadCache()
    {
        if (_cacheLoaded)
            return;
        _cacheLoaded = true;
        try
        {
            if (!File.Exists(_cachePath))
                return;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, ProjectScan>>(
                File.ReadAllText(_cachePath));
            if (loaded != null)
                _cache = new Dictionary<string, ProjectScan>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _log.Warn("rule", $"台账缓存读取失败(将重扫): {ex.Message}");
        }
    }

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(_cache));
        }
        catch (Exception ex)
        {
            _log.Warn("rule", $"台账缓存写入失败(不影响本次结果): {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- 辅助

    private async Task<List<WorktreeInfo>> ResolveTargetsAsync(string? project)
    {
        if (!string.IsNullOrWhiteSpace(project))
        {
            var resolved = await _projects.ResolveWorktreeAsync(project).ConfigureAwait(false);
            return resolved.Success && resolved.Worktree != null ? [resolved.Worktree] : [];
        }

        var (git, worktrees) = await _projects.ListWorktreesAsync().ConfigureAwait(false);
        return git.Success
            ? worktrees.Where(w => Directory.Exists(w.WorktreePath)).ToList()
            : [];
    }

    private static IReadOnlyList<string> SplitPaths(string output)
        => output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().Replace('\\', '/'))
            .Where(p => p.Length > 0)
            .ToList();

    /// <summary>路径 → 台账格式键;复合扩展(如 .tar.gz)取末段,无扩展名归入专用桶。</summary>
    public static string FormatOf(string path)
    {
        var name = path;
        var slash = name.LastIndexOf('/');
        if (slash >= 0)
            name = name[(slash + 1)..];
        var dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1 ? NoExtension : "*" + name[dot..].ToLowerInvariant();
    }

    private static string RollupDirectory(string path)
    {
        var slash = path.LastIndexOf('/');
        if (slash < 0)
            return "(项目根)";
        var dir = path[..slash];
        var segments = dir.Split('/');
        return segments.Length <= DirectoryRollupSegments
            ? dir
            : string.Join('/', segments.Take(DirectoryRollupSegments));
    }

    private enum FileKind { Tracked, Untracked, Ignored }

    private sealed class MergedFormat
    {
        public HashSet<string> Projects { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Tracked;
        public int Ignored;
        public int Untracked;
        public int DeclaredIn;
        public long MaxBytes;
        public long TotalBytes;
        public bool? Track;
        public bool? Lfs;
        public bool? Lf;
        public bool Divergent;
        public bool BinarySniff;
    }

    /// <summary>单项目扫描结果(可序列化,进缓存)。</summary>
    public sealed record ProjectScan(
        string Project,
        string Key,
        string Depth,
        int Tracked,
        int Ignored,
        int Untracked,
        int LfsPointers,
        Dictionary<string, FormatStat> Formats,
        Dictionary<string, DirectoryStat> Directories,
        Dictionary<string, DeclaredDto> Declared);

    public sealed class FormatStat
    {
        public int Tracked { get; set; }
        public int Ignored { get; set; }
        public int Untracked { get; set; }
        public long MaxBytes { get; set; }
        public long TotalBytes { get; set; }
        public long LargestBytes { get; set; }
        public string Sample { get; set; } = "";

        /// <summary>样本内容嗅探结果:true=二进制。用于纠正扩展名清单的误判(见 SniffBinary)。</summary>
        public bool BinarySniff { get; set; }
    }

    public sealed class DirectoryStat
    {
        public int Tracked { get; set; }
        public int Ignored { get; set; }
        public int Untracked { get; set; }
    }

    /// <summary>
    /// 内容嗅探:读样本前 512 字节判断是否二进制。
    /// 存在的意义是**数据胜过硬编码清单**——实测本库 .asm/.cfg 实为 Solid Edge 的
    /// OLE2 复合文档(D0 CF 11 E0),若按扩展名当文本做 LF 规范化会损坏 1871 个 CAD 文件。
    /// </summary>
    private static bool SniffBinary(string root, string relativePath)
    {
        try
        {
            var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            using var stream = File.OpenRead(full);
            Span<byte> head = stackalloc byte[512];
            var read = stream.Read(head);
            if (read <= 0)
                return false;

            head = head[..read];

            // 含 NUL 即判定二进制(UTF-8/GBK 文本不含 NUL)
            foreach (var b in head)
            {
                if (b == 0)
                    return true;
            }

            // 常见二进制签名:OLE2(SolidEdge/Office 旧格式)、ZIP/OOXML、PDF、PNG
            ReadOnlySpan<byte> ole2 = [0xD0, 0xCF, 0x11, 0xE0];
            return head.Length >= 4 && head[..4].SequenceEqual(ole2);
        }
        catch (Exception)
        {
            return false; // 读不到就不下二进制结论
        }
    }

    public sealed record DeclaredDto(bool Track, bool Lfs, bool Lf);
}
