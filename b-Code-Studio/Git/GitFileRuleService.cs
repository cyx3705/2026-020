using System.IO;
using System.Text;
using HistoryVulcan.Core.Storage;

namespace HistoryJanus.Git;

/// <summary>一个项目的"不纳入仓库"规则落地状态。</summary>
public sealed record ExcludeRuleState(
    string Project,
    bool BlockCurrent,
    int IgnoredFileCount,
    int TrackedButExcludedCount,
    string Detail);

/// <summary>整库共用的排除清单及各项目的落地情况。</summary>
public sealed record ExcludeRuleReport(
    IReadOnlyList<string> Suffixes,
    IReadOnlyList<string> Directories,
    IReadOnlyList<ExcludeRuleState> Projects);

/// <summary>某个仓里一个走 LFS 指针的文件。</summary>
public sealed record LfsTrackedFile(string RelativePath, string FormattedSize);

/// <summary>
/// 某个仓的 LFS 实况。<see cref="Available"/> 为假说明本机没装 git-lfs，
/// 这与「装了但一个文件都没走 LFS」是两回事，规则面要分开说。
/// </summary>
public sealed record LfsReport(
    string Project,
    bool Available,
    IReadOnlyList<LfsTrackedFile> Files);

/// <summary>
/// 提交链路刷写 .gitignore 托管块的窄接口。
/// 单独抽出来只为打断依赖环：GitFileRuleService 需要 ProjectService 解析项目路径，
/// 而提交链路又要回头刷规则；ProjectService 只依赖这个接口，且可为空。
/// </summary>
public interface IExcludeRuleWriter
{
    Task<(bool Success, bool Changed, string Message)> EnsureIgnoreAsync(
        string root, CancellationToken cancellation = default);
}

/// <summary>
/// Git 文件规则的唯一形态：一份**全库共用**的「不纳入仓库」清单。
///
/// 4.x 曾按格式逐条维护 Git/LFS/LF 三态规则表，并要求人工执行 sync 下发。
/// 实践证明那套规则面本身就是故障源：137 条按扩展名的 LFS 通配把 *.asm / *.baml
/// 这类文本也塞进 LFS，最终 11 GB LFS 占用、推送被 GitHub pre-receive 拒收。
/// 5.0.0 起规则只回答一个问题——这个后缀/目录要不要进仓库；LFS 完全退出规则面，
/// 只在提交链路里对超过 GitHub 100MB 硬限的**具体文件**征求人工同意。
///
/// 清单存在设置里而不是各仓文件里：全库一致才不消耗认知，且提交链路会自动把它
/// 幂等刷进各仓 .gitignore 托管块，不再需要任何手动下发命令。
/// </summary>
public sealed class GitFileRuleService : IExcludeRuleWriter
{
    public const string KeyExcludeSuffixes = "proj.excludesuffixes";

    /// <summary>
    /// 默认清单来自 2026-08 全库推送的实测：这些目录与后缀全是可再生产物或 IDE 状态，
    /// 入库只会撑大仓库并把推送顶到传输上限。目录项以 / 结尾。
    /// </summary>
    public const string DefaultExcludeSuffixes =
        "bin/, obj/, venv/, .venv/, __pycache__/, .vs/, .idea/, node_modules/, " +
        ".pytest_cache/, .mypy_cache/, tools/jdk/, packages/, " +
        "*.dll, *.pdb, *.lib, *.exp, *.ilk, *.idb, *.obj, *.cache, " +
        "*.user, *.suo, *.tmp, *.temp, *.log, *.bak, *.swp, *.xlk, *.autosave, Thumbs.db, .DS_Store";

    /// <summary>
    /// 正式消费快照目录前缀。`z-*` 下的内容由发布管线刻意入库
    /// （例如 z-HistoryVulcan/host 的宿主 DLL、各模块的 z-&lt;模块&gt;/*.dll），
    /// 是跨项目消费的权威产物，绝不能被排除清单顺手删出索引。
    /// </summary>
    private const string SnapshotPrefix = "z-";

    private const string ManagedBegin = "# HistoryJanus managed begin";
    private const string ManagedEnd = "# HistoryJanus managed end";

    private static readonly string[] ManagedHeader =
    [
        "# 本块由 HistoryJanus 按设置 proj.excludesuffixes 自动生成，全库一致。",
        "# 不要手工编辑块内内容；改清单请用 janus.gitrule.excludes。",
        "# 块外内容属于本项目自己，Janus 逐字保留。",
    ];

    /// <summary>
    /// 块尾的豁免：必须排在全部排除项之后，gitignore 才会让后面的否定规则生效。
    /// 这是系统不变量而不是用户选项，所以不进清单——把 dll 之类写进清单的人
    /// 不应该因此把正式快照删出索引。
    /// </summary>
    private static readonly string[] ManagedFooter =
    [
        "",
        "# 正式消费快照由发布管线刻意入库，不受上面任何排除项影响。",
        "!" + SnapshotPrefix + "*/",
        "!" + SnapshotPrefix + "*/**",
    ];

    private readonly ISettingsService _settings;
    private readonly ProjectService _projects;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public GitFileRuleService(ProjectService projects, ISettingsService settings)
    {
        _projects = projects;
        _settings = settings;
        if (_settings.Get(KeyExcludeSuffixes) == null)
            _settings.Set(KeyExcludeSuffixes, DefaultExcludeSuffixes);
    }

    public string RawExcludeList => _settings.Get(KeyExcludeSuffixes) ?? DefaultExcludeSuffixes;

    /// <summary>后缀项（*.xxx 或裸文件名），不含目录项。</summary>
    public IReadOnlyList<string> Suffixes => Parse(RawExcludeList).Suffixes;

    /// <summary>目录项（以 / 结尾），生成时统一加 **/ 前缀以匹配任意层级。</summary>
    public IReadOnlyList<string> Directories => Parse(RawExcludeList).Directories;

    /// <summary>
    /// 改写整库共用清单。逐项校验后整体接受或整体拒绝，不做部分写入。
    /// </summary>
    public (bool Success, string Message) SetExcludeList(string raw)
    {
        var parsed = Parse(raw);
        if (parsed.Invalid.Count > 0)
            return (false, "以下条目不合法，清单未改动:\n  " + string.Join("\n  ", parsed.Invalid));
        if (parsed.Suffixes.Count == 0 && parsed.Directories.Count == 0)
            return (false, "清单不能为空；要停用排除请只保留少量条目而不是清空");

        var canonical = string.Join(", ", parsed.Directories.Concat(parsed.Suffixes));
        _settings.Set(KeyExcludeSuffixes, canonical);
        return (true,
            $"清单已更新: {parsed.Directories.Count} 个目录 + {parsed.Suffixes.Count} 个后缀。" +
            "下次提交时自动刷入各仓 .gitignore 托管块。\n  " + canonical);
    }

    /// <summary>
    /// 把当前清单幂等刷进该仓 .gitignore 的托管块。提交链路每次调用，
    /// 所以不需要任何手动下发命令；块外内容与其它块逐字保留。
    /// </summary>
    public async Task<(bool Success, bool Changed, string Message)> EnsureIgnoreAsync(
        string root, CancellationToken cancellation = default)
    {
        var desired = BuildManagedBlock();
        await _writeGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(root, ".gitignore");
            var original = File.Exists(path)
                ? await File.ReadAllTextAsync(path, cancellation).ConfigureAwait(false)
                : string.Empty;
            var rewritten = RewriteManagedBlock(original, desired);
            if (rewritten == original)
                return (true, false, ".gitignore 托管块已是最新");
            await File.WriteAllTextAsync(path, rewritten, cancellation).ConfigureAwait(false);
            return (true, true, ".gitignore 托管块已刷新为全库统一清单");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, false, $"写入 .gitignore 失败: {ex.Message}");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// 只读：**这个仓里实际走 LFS 指针的是哪几个文件**。
    ///
    /// 规则面上原先写的是一条策略——「单个文件超过 100MB 转 LFS」。那句话对每个仓
    /// 都一样，因此看了也不知道自己这个仓到底有没有 LFS、有哪几个；而这恰恰是
    /// 2026-08 那次 11GB LFS 占用事故之后最该一眼看到的事实。现在改为直接列文件。
    /// </summary>
    public async Task<(bool Success, string Message, LfsReport? Report)> ListLfsAsync(
        string? project, CancellationToken cancellation = default)
    {
        var resolved = await _projects.ResolveWorktreeAsync(project ?? "").ConfigureAwait(false);
        if (!resolved.Success || resolved.Worktree == null)
            return (false, resolved.Message, null);

        var repository = resolved.Worktree.WorktreePath;
        if (!await WorktreeLfsHelper.IsGitLfsAvailableAsync(repository).ConfigureAwait(false))
            return (true, "本机未安装 git-lfs", new LfsReport(resolved.Worktree.BranchName, false, []));

        // -s 给出「路径 (大小)」；大小是 LFS 自己记的实体大小，不必再逐个 stat 工作树。
        var listed = await GitRunner.RunAsync(repository, ["lfs", "ls-files", "-s"], cancellation)
            .ConfigureAwait(false);
        if (!listed.Success)
            return (false, $"读取 LFS 清单失败:\n{listed.Output}", null);

        var files = listed.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseLfsLine)
            .Where(file => file.RelativePath.Length > 0)
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return (true,
            files.Count == 0
                ? $"{resolved.Worktree.BranchName}: 本仓没有文件走 LFS"
                : $"{resolved.Worktree.BranchName}: {files.Count} 个文件走 LFS",
            new LfsReport(resolved.Worktree.BranchName, true, files));
    }

    /// <summary>
    /// <c>git lfs ls-files -s</c> 的一行形如
    /// <c>2f1a3b4c5d * z-Publish/big.zip (128 MB)</c>。
    /// 只取路径与括号里的大小；对不上格式就只留整行当路径，不猜。
    /// </summary>
    private static LfsTrackedFile ParseLfsLine(string line)
    {
        var marker = line.IndexOf(' ');
        var rest = marker < 0 ? line : line[(marker + 1)..].TrimStart();
        if (rest.StartsWith("* ", StringComparison.Ordinal) || rest.StartsWith("- ", StringComparison.Ordinal))
            rest = rest[2..];
        var size = "";
        var open = rest.LastIndexOf(" (", StringComparison.Ordinal);
        if (open > 0 && rest.EndsWith(')'))
        {
            size = rest[(open + 2)..^1];
            rest = rest[..open];
        }
        return new LfsTrackedFile(rest.Trim(), size);
    }

    /// <summary>
    /// 只读：清单本身，以及各项目托管块是否最新、有多少文件被排除、
    /// 有多少**已被跟踪却命中排除**（这类需要人工决定是否 git rm --cached）。
    /// </summary>
    public async Task<(bool Success, string Message, ExcludeRuleReport? Report)> ListAsync(
        string? project, CancellationToken cancellation = default)
    {
        List<WorktreeInfo> targets;
        if (!string.IsNullOrWhiteSpace(project))
        {
            var one = await _projects.ResolveWorktreeAsync(project).ConfigureAwait(false);
            if (!one.Success || one.Worktree == null)
                return (false, one.Message, null);
            targets = [one.Worktree];
        }
        else
        {
            var (git, worktrees) = await _projects.ListWorktreesAsync().ConfigureAwait(false);
            if (!git.Success)
                return (false, $"获取项目清单失败:\n{git.Output}", null);
            targets = worktrees.Where(w => Directory.Exists(w.WorktreePath)).ToList();
        }

        var desired = BuildManagedBlock();
        var states = new List<ExcludeRuleState>(targets.Count);
        foreach (var target in targets)
        {
            var path = Path.Combine(target.WorktreePath, ".gitignore");
            var text = File.Exists(path)
                ? await File.ReadAllTextAsync(path, cancellation).ConfigureAwait(false)
                : string.Empty;
            var current = RewriteManagedBlock(text, desired) == text;

            var ignored = await GitRunner.RunAsync(target.WorktreePath,
                ["ls-files", "--others", "--ignored", "--exclude-standard", "-z"],
                cancellation).ConfigureAwait(false);
            var stale = await GitRunner.RunAsync(target.WorktreePath,
                ["ls-files", "--cached", "--ignored", "--exclude-standard", "-z"],
                cancellation).ConfigureAwait(false);
            var ignoredCount = CountNul(ignored);
            var staleCount = CountNul(stale);
            states.Add(new ExcludeRuleState(target.BranchName, current, ignoredCount, staleCount,
                current
                    ? staleCount > 0
                        ? $"{staleCount} 个文件已被跟踪却命中排除，需人工决定是否移出索引"
                        : "一致"
                    : "托管块落后于当前清单，下次提交会自动刷新"));
        }

        var parsed = Parse(RawExcludeList);
        var text2 = new StringBuilder();
        text2.Append($"全库共用排除清单: {parsed.Directories.Count} 个目录 + {parsed.Suffixes.Count} 个后缀");
        text2.Append($"\n  {string.Join(", ", parsed.Directories.Concat(parsed.Suffixes))}");
        text2.Append($"\n项目落地情况({states.Count} 个):");
        foreach (var state in states)
            text2.Append($"\n  {state.Project,-28} {(state.BlockCurrent ? "✓" : "✗")} " +
                         $"排除 {state.IgnoredFileCount} 个文件  {state.Detail}");
        return (true, text2.ToString(),
            new ExcludeRuleReport(parsed.Suffixes, parsed.Directories, states));
    }

    private IReadOnlyList<string> BuildManagedBlock()
    {
        var parsed = Parse(RawExcludeList);
        var lines = new List<string>(ManagedHeader);
        // 目录项统一加 **/ 前缀：不带前缀时 git 只按仓库根匹配，
        // 嵌套的 a17-xxx/tools/jdk/ 会漏掉（2026-08 实测踩过）。
        foreach (var dir in parsed.Directories)
            lines.Add(dir.TrimEnd('/').Contains('/') && !dir.StartsWith("**/", StringComparison.Ordinal)
                ? "**/" + dir
                : dir);
        lines.AddRange(parsed.Suffixes);
        lines.AddRange(ManagedFooter);
        return lines;
    }

    private static string RewriteManagedBlock(string original, IReadOnlyList<string> block)
    {
        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = original.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n').ToList();
        var begin = lines.FindIndex(line => line.Trim() == ManagedBegin);
        var end = begin >= 0
            ? lines.FindIndex(begin, line => line.Trim() == ManagedEnd)
            : -1;

        var rendered = new List<string> { ManagedBegin };
        rendered.AddRange(block);
        rendered.Add(ManagedEnd);

        if (begin >= 0 && end > begin)
        {
            lines.RemoveRange(begin, end - begin + 1);
            lines.InsertRange(begin, rendered);
        }
        else
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0)
                lines.Add(string.Empty);
            lines.AddRange(rendered);
            lines.Add(string.Empty);
        }
        return string.Join(newline, lines);
    }

    private static (List<string> Suffixes, List<string> Directories, List<string> Invalid) Parse(string? raw)
    {
        var suffixes = new List<string>();
        var directories = new List<string>();
        var invalid = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in (raw ?? string.Empty)
                 .Split([',', ';', '\n', '\r', '\t', ' '], StringSplitOptions.TrimEntries
                                                           | StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith('#'))
                continue;
            if (token.IndexOfAny(['\\', ':', '"', '\'', '|', '<', '>']) >= 0)
            {
                invalid.Add($"{token} —— 不允许反斜杠、盘符或引号，目录分隔一律用 /");
                continue;
            }
            if (!seen.Add(token))
                continue;
            if (token.EndsWith('/'))
                directories.Add(token);
            else if (token.StartsWith("*.", StringComparison.Ordinal) && token.Length > 2)
                suffixes.Add(token);
            else if (!token.Contains('*') && !token.Contains('/'))
                suffixes.Add(token); // 裸文件名，如 Thumbs.db
            else
                invalid.Add($"{token} —— 只接受 *.后缀、裸文件名，或以 / 结尾的目录");
        }
        return (suffixes, directories, invalid);
    }

    private static int CountNul(GitResult result)
        => result.Success
            ? result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Length
            : 0;
}
