namespace HistoryJanus.Git;

/// <summary>一条格式处置建议。Track=false 即建议忽略。</summary>
public sealed record RuleSuggestion(
    string Pattern,
    bool Track,
    bool Lfs,
    bool Lf,
    string Reason,
    int AffectedFiles);

/// <summary>
/// 规则建议引擎：只建议、不自动应用；未知格式一律留白不猜。
/// 分类清单为代码内常量，避免出现与实现行为分离的配置入口。
/// </summary>
public static class RuleSuggestionEngine
{
    /// <summary>派生/临时件:构建产物、锁文件、软件缓存——建议忽略。</summary>
    private static readonly HashSet<string> Junk = new(StringComparer.OrdinalIgnoreCase)
    {
        // 通用构建与缓存
        ".obj", ".pdb", ".ilk", ".exp", ".idb", ".tlog", ".log", ".tmp", ".temp",
        ".cache", ".bak", ".swp", ".suo", ".user", ".userosscache", ".sln.docstates",
        // 锁文件
        ".lck", ".lock", ".flk", ".slk", ".xlk", ".elk",
        // EPLAN / Solid Edge 派生件(实测:.eod 2.06GB / .eox 367MB 全库污染)
        ".eod", ".eox",
        // Unity / 引擎派生
        ".unityproj", ".booproj", ".pidb", ".mdb",
        // 其他
        ".ds_store", ".thumbs", ".crdownload", ".partial",
    };

    /// <summary>文本格式:建议纳入 Git 并统一 LF。</summary>
    private static readonly HashSet<string> Text = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".md", ".txt", ".json", ".xml", ".yml", ".yaml", ".csv", ".ini",
        ".config", ".props", ".targets", ".csproj", ".sln", ".editorconfig", ".gitignore",
        ".gitattributes", ".c", ".h", ".cpp", ".hpp", ".py", ".js", ".ts", ".css", ".html",
        ".htm", ".sh", ".ps1", ".bat", ".cmd", ".sql", ".java", ".go", ".rs", ".toml",
        ".ino", ".m", ".s", ".vb", ".fs", ".lua", ".r", ".tex", ".bib", ".meta",
    };

    /// <summary>二进制格式:纳入 Git;是否走 LFS 由体积判据决定。</summary>
    private static readonly HashSet<string> Binary = new(StringComparer.OrdinalIgnoreCase)
    {
        // 图像与文档
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".ico", ".svg",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".vsd", ".vsdx",
        // 音视频
        ".mp4", ".mov", ".avi", ".mkv", ".wmv", ".flv", ".mp3", ".wav", ".flac",
        // CAD / 三维(实测本库 .asm/.cfg 为 Solid Edge OLE2 二进制,不可当文本)
        ".par", ".asm", ".cfg", ".psm", ".dft", ".prt", ".sldprt", ".sldasm", ".slddrw",
        ".stp", ".step", ".igs", ".iges", ".jt", ".stl", ".dwg", ".dxf", ".catpart",
        ".f3d", ".ipt", ".iam", ".x_t", ".x_b", ".3mf", ".obj3d", ".fbx", ".blend",
        // 归档与二进制产物
        ".zip", ".rar", ".7z", ".tar", ".gz", ".exe", ".dll", ".lib", ".so", ".bin",
        ".hex", ".elf", ".db", ".sqlite", ".mcam", ".prproj", ".psd", ".ai",
    };

    /// <summary>构建产物类二进制:虽是二进制,但属派生物,建议忽略而非入库。</summary>
    private static readonly HashSet<string> DerivedBinary = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".lib", ".so", ".pdb", ".obj",
    };

    /// <summary>
    /// 对一行台账给出建议;返回 null = 不建议(未知格式,留白待人工判定)。
    /// lfsThresholdBytes 取 proj.rejectmb(默认 100MB):最大单件超阈即建议 LFS。
    /// </summary>
    public static RuleSuggestion? Suggest(FormatRow row, long lfsThresholdBytes)
    {
        // 无扩展名与 other 桶不按格式建议(目录规则处置,§3.5)
        if (row.Format == FormatInventoryService.NoExtension)
            return null;

        var ext = row.Format.StartsWith("*.", StringComparison.Ordinal)
            ? row.Format[1..]
            : row.Format;

        if (DerivedBinary.Contains(ext))
            return new RuleSuggestion(row.Format, Track: false, Lfs: false, Lf: false,
                "构建产物,不应入库", row.UndecidedCount);

        if (Junk.Contains(ext))
            return new RuleSuggestion(row.Format, Track: false, Lfs: false, Lf: false,
                "派生/临时件,不应入库", row.UndecidedCount);

        // 内容嗅探优先于扩展名清单:实测 .asm/.cfg 在本库是 Solid Edge 的 OLE2 二进制,
        // 若按清单当文本做 LF 规范化会损坏文件。数据胜过硬编码。
        if (Text.Contains(ext) && !row.BinarySniff)
            return new RuleSuggestion(row.Format, Track: true, Lfs: false, Lf: true,
                "文本源码,统一 LF", row.UndecidedCount);

        if (Binary.Contains(ext) || row.BinarySniff)
        {
            var big = row.MaxBytes >= lfsThresholdBytes;
            var sniffNote = row.BinarySniff && Text.Contains(ext) ? "(内容嗅探判定为二进制,已否决文本分类)" : "";
            return new RuleSuggestion(row.Format, Track: true, Lfs: big, Lf: false,
                (big ? $"大二进制(最大 {Mb(row.MaxBytes)}MB ≥ 阈值),走 LFS" : "二进制,直接入库") + sniffNote,
                row.UndecidedCount);
        }

        return null; // 未知格式宁留未决也不猜。
    }

    private static string Mb(long bytes) => (bytes / (1024.0 * 1024)).ToString("0.#");
}
