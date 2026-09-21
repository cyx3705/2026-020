using System.Text;

namespace HistoryJanus.Git;

/// <summary>
/// LFS 规则面的纯函数部分：两个托管块的读写、精确路径的锚定与转义、找出不合规的 LFS 行。
/// 不启进程也不碰磁盘，门禁可以逐条钉住。
///
/// 规则只有一条（5.11.0）：**不到 100MB 的文件一律不走 LFS**。
/// 走 LFS 的只能是 ≥100MB 的具体文件，而且只能写在 Janus 的 lfs 托管块里；
/// 托管块之外的任何 <c>filter=lfs</c>——按扩展名的通配也好、不带前导斜杠的"精确"路径也好——
/// 都算违规。后者会匹配任意层级的同名文件，2026-09 已经把一张 1KB 的 logo 存成了指针。
///
/// ≥100MB 的文件只有两种去向，都由人在「LFS 规则」子页上逐个决定：
/// 写进 .gitattributes 的 lfs 块（走指针），或写进 .gitignore 的 oversize 块（不再纳入 git）。
/// 两种都不改写历史。
/// </summary>
public static class LfsPolicy
{
    /// <summary>判据就是 GitHub 的单文件硬限。不是偏好，所以不进设置。</summary>
    public const long ThresholdBytes = ProjectService.GitHubFileLimitBytes;

    public const string AttributesBegin = "# HistoryJanus lfs begin";
    public const string AttributesEnd = "# HistoryJanus lfs end";
    public const string IgnoreBegin = "# HistoryJanus oversize begin";
    public const string IgnoreEnd = "# HistoryJanus oversize end";

    private const string LfsAttributes = "filter=lfs diff=lfs merge=lfs -text";

    private static readonly string[] AttributesHeader =
    [
        "# 本块由 HistoryJanus 维护，只列 ≥100MB 的具体文件（前导 / 锚定到仓库根）。",
        "# 不到 100MB 的文件一律不走 LFS；本块之外不允许出现 filter=lfs。",
        "# 改决定请用 Janus「LFS 规则」子页或 janus.gitrule.lfsset。",
    ];

    private static readonly string[] IgnoreHeader =
    [
        "# 本块由 HistoryJanus 维护：≥100MB、决定不再纳入 git 的具体文件。本地文件保留。",
        "# 它必须留在文件最后——排在前面会被 managed 块尾部的 !z-*/ 豁免重新放回来。",
    ];

    /// <summary>
    /// 上一轮「极保守」修复写进各仓 .gitattributes 顶部的说明。它说的是
    /// 「已有指针保留在 LFS」，与现行规则正好相反，修复时整段换掉，免得下一个人照着它做。
    /// </summary>
    private static readonly string[] LegacyHeader =
    [
        "# 极保守 LFS 策略：不按扩展名批量套 LFS。",
        "# 旧的按扩展名通配（*.asm / *.baml 这类文本也在内）是 11GB LFS 占用",
        "# 与推送被 GitHub pre-receive 拒收的根因。",
        "# 下面只逐条列出**已经是 LFS 指针**的现存文件，保证这些指针不失效；",
        "# 新文件一律不自动进 LFS——只有单个文件超过 GitHub 100MB 硬限时，",
        "# 才由提交链路逐个征求人工同意后按精确路径追加。",
    ];

    private static readonly string[] ReplacementHeader =
    [
        "# LFS 规则：不到 100MB 的文件一律不走 LFS。",
        "# ≥100MB 的具体文件由下面的 HistoryJanus lfs 托管块逐条列出，其余行不得出现 filter=lfs。",
    ];

    /// <summary>仓库内相对路径的统一形态：正斜杠、无前导 ./ 或 /。</summary>
    public static string Normalize(string relativePath)
    {
        var path = relativePath.Trim().Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal))
            path = path[2..];
        return path.TrimStart('/');
    }

    /// <summary>
    /// 正式消费快照（<c>z-*</c> 顶层目录）必须入库，不能选「不纳入 git」。
    /// 它超限时唯一的出路是 LFS 指针。
    /// </summary>
    public static bool IsSnapshotPath(string relativePath)
    {
        var path = Normalize(relativePath);
        var slash = path.IndexOf('/');
        var top = slash < 0 ? path : path[..slash];
        return slash > 0 && top.StartsWith("z-", StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- 锚定与转义

    /// <summary>
    /// .gitattributes 里的一行：前导斜杠锚定到仓库根，通配字符转义；
    /// 含空白或引号时整条用 C 风格引号包起来（git 读 attributes 时支持）。
    /// </summary>
    public static string AttributesLine(string relativePath)
    {
        var pattern = "/" + EscapeGlob(Normalize(relativePath));
        if (pattern.IndexOfAny([' ', '\t', '"']) >= 0)
            pattern = "\"" + pattern.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        return pattern + " " + LfsAttributes;
    }

    /// <summary>
    /// .gitignore 里的一行。gitignore 不认引号，空格原样即可；
    /// 只有行尾空格会被吞掉，要逐个转义。
    /// </summary>
    public static string IgnoreLine(string relativePath)
    {
        var pattern = "/" + EscapeGlob(Normalize(relativePath));
        var trimmed = pattern.TrimEnd(' ');
        var trailing = pattern.Length - trimmed.Length;
        return trailing == 0 ? pattern : trimmed + string.Concat(Enumerable.Repeat("\\ ", trailing));
    }

    private static string EscapeGlob(string path)
    {
        var builder = new StringBuilder(path.Length + 4);
        foreach (var ch in path)
        {
            if (ch is '\\' or '*' or '?' or '[' or ']')
                builder.Append('\\');
            builder.Append(ch);
        }
        return builder.ToString();
    }

    private static string UnescapeGlob(string pattern)
    {
        var builder = new StringBuilder(pattern.Length);
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '\\' && i + 1 < pattern.Length)
                i++;
            builder.Append(pattern[i]);
        }
        return builder.ToString();
    }

    /// <summary>
    /// 拆出一行 attributes 的模式与属性。模式可以是 C 风格引号包起来的。
    /// 注释与空行返回 null。
    /// </summary>
    public static (string Pattern, List<string> Attributes)? ParseAttributesLine(string line)
    {
        var text = line.Trim();
        if (text.Length == 0 || text.StartsWith('#'))
            return null;

        string pattern;
        string rest;
        if (text.StartsWith('"'))
        {
            var builder = new StringBuilder();
            var i = 1;
            for (; i < text.Length && text[i] != '"'; i++)
            {
                if (text[i] == '\\' && i + 1 < text.Length)
                    i++;
                builder.Append(text[i]);
            }
            pattern = builder.ToString();
            rest = i + 1 < text.Length ? text[(i + 1)..] : "";
        }
        else
        {
            var space = text.IndexOfAny([' ', '\t']);
            pattern = space < 0 ? text : text[..space];
            rest = space < 0 ? "" : text[space..];
        }

        var attributes = rest.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).ToList();
        return (pattern, attributes);
    }

    private static bool IsLfsAttribute(string attribute)
        => attribute is "filter=lfs" or "diff=lfs" or "merge=lfs";

    /// <summary>托管块里一行对应的仓库相对路径；不是本块写出的形态就返回 null。</summary>
    public static string? PathOfAttributesLine(string line)
    {
        var parsed = ParseAttributesLine(line);
        if (parsed is not { } entry || !entry.Pattern.StartsWith('/'))
            return null;
        return UnescapeGlob(entry.Pattern[1..]);
    }

    public static string? PathOfIgnoreLine(string line)
    {
        var text = line.Trim();
        if (text.Length == 0 || text.StartsWith('#') || !text.StartsWith('/'))
            return null;
        // 行尾未转义的空格 git 会忽略；转义过的（\ ）是路径的一部分，交给 UnescapeGlob。
        var raw = line.TrimStart().TrimEnd('\r');
        while (raw.EndsWith(' ') && !raw.EndsWith("\\ ", StringComparison.Ordinal))
            raw = raw[..^1];
        return UnescapeGlob(raw[1..]);
    }

    // ---------------------------------------------------------------- 托管块

    public static IReadOnlyList<string> ReadLfsPaths(string attributesText)
        => BlockLines(attributesText, AttributesBegin, AttributesEnd)
            .Select(PathOfAttributesLine).OfType<string>().ToList();

    public static IReadOnlyList<string> ReadIgnoredPaths(string ignoreText)
        => BlockLines(ignoreText, IgnoreBegin, IgnoreEnd)
            .Select(PathOfIgnoreLine).OfType<string>().ToList();

    /// <summary>
    /// 把 lfs 块改写成给定的路径集合。块已存在就原位替换，否则追加在文件尾。
    /// 路径集合为空时整块删掉——空块只会让人以为这里还管着什么。
    /// </summary>
    public static string WriteLfsBlock(string attributesText, IEnumerable<string> paths)
    {
        var lines = Sorted(paths).Select(AttributesLine).ToList();
        return RewriteBlock(attributesText, AttributesBegin, AttributesEnd,
            lines.Count == 0 ? null : AttributesHeader.Concat(lines).ToList(), moveToEnd: false);
    }

    /// <summary>
    /// oversize 块**总是挪到文件最后**：gitignore 以最后一条匹配为准，
    /// managed 块尾部的 <c>!z-*/**</c> 豁免如果排在它后面，会把它盖掉。
    /// </summary>
    public static string WriteIgnoreBlock(string ignoreText, IEnumerable<string> paths)
    {
        var lines = Sorted(paths).Select(IgnoreLine).ToList();
        return RewriteBlock(ignoreText, IgnoreBegin, IgnoreEnd,
            lines.Count == 0 ? null : IgnoreHeader.Concat(lines).ToList(), moveToEnd: true);
    }

    private static List<string> Sorted(IEnumerable<string> paths)
        => paths.Select(Normalize).Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

    private static IEnumerable<string> BlockLines(string text, string begin, string end)
    {
        var lines = SplitLines(text);
        var start = lines.FindIndex(line => line.Trim() == begin);
        if (start < 0)
            return [];
        var stop = lines.FindIndex(start, line => line.Trim() == end);
        return stop < 0 ? [] : lines.Skip(start + 1).Take(stop - start - 1);
    }

    private static string RewriteBlock(
        string original, string begin, string end, IReadOnlyList<string>? body, bool moveToEnd)
    {
        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = SplitLines(original);
        var start = lines.FindIndex(line => line.Trim() == begin);
        var stop = start >= 0 ? lines.FindIndex(start, line => line.Trim() == end) : -1;
        // 没有块、也不要块：原样交回，不顺手规整空行。
        if (body == null && (start < 0 || stop <= start))
            return original;
        var at = -1;
        if (start >= 0 && stop > start)
        {
            lines.RemoveRange(start, stop - start + 1);
            // 删块后留下的那一行空行也一并收掉，反复改写不累积空行。
            if (start < lines.Count && lines[start].Trim().Length == 0
                && (start == 0 || lines[start - 1].Trim().Length == 0))
                lines.RemoveAt(start);
            at = moveToEnd ? -1 : start;
        }

        if (body != null)
        {
            var rendered = new List<string> { begin };
            rendered.AddRange(body);
            rendered.Add(end);
            if (at < 0)
            {
                while (lines.Count > 0 && lines[^1].Trim().Length == 0)
                    lines.RemoveAt(lines.Count - 1);
                if (lines.Count > 0)
                    lines.Add(string.Empty);
                lines.AddRange(rendered);
            }
            else
            {
                lines.InsertRange(at, rendered);
            }
        }

        while (lines.Count > 0 && lines[^1].Trim().Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return lines.Count == 0 ? string.Empty : string.Join(newline, lines) + newline;
    }

    private static List<string> SplitLines(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    // ---------------------------------------------------------------- 违规

    /// <summary>
    /// lfs 托管块之外、仍带 <c>filter=lfs</c> 的规则行数。任何一条都违规。
    /// 嵌套目录里的 .gitattributes 没有托管块，其中每一条 LFS 行都算。
    /// </summary>
    public static int CountForeignLfsRules(string attributesText)
    {
        var count = 0;
        var inside = false;
        foreach (var line in SplitLines(attributesText))
        {
            var trimmed = line.Trim();
            if (trimmed == AttributesBegin) { inside = true; continue; }
            if (trimmed == AttributesEnd) { inside = false; continue; }
            if (!inside && ParseAttributesLine(line) is { } entry && entry.Attributes.Any(IsLfsAttribute))
                count++;
        }
        return count;
    }

    /// <summary>
    /// 去掉托管块之外所有行上的 LFS 属性。**保留 <c>-text</c> 与其它属性**：
    /// 这些多是 CAD/二进制文件，丢了 <c>-text</c>，<c>* text=auto</c> 可能把没有 NUL 字节的
    /// 二进制当文本做换行转换。只剩一个模式、没有任何属性的行整行删除。
    /// 顺手把上一轮「极保守」的说明换成现行规则。
    /// </summary>
    public static string StripForeignLfsRules(string attributesText, out int stripped)
    {
        stripped = 0;
        var newline = attributesText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = SplitLines(attributesText);
        var output = new List<string>(lines.Count);
        var inside = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed == AttributesBegin) inside = true;
            if (inside || ParseAttributesLine(line) is not { } entry || !entry.Attributes.Any(IsLfsAttribute))
            {
                output.Add(line);
                if (trimmed == AttributesEnd) inside = false;
                continue;
            }

            stripped++;
            var kept = entry.Attributes.Where(attribute => !IsLfsAttribute(attribute)).ToList();
            if (kept.Count == 0)
                continue;
            var raw = line.TrimStart();
            var patternText = raw.StartsWith('"')
                ? raw[..(QuotedLength(raw))]
                : raw[..(raw.IndexOfAny([' ', '\t']) is var space and >= 0 ? space : raw.Length)];
            output.Add(patternText + " " + string.Join(' ', kept));
        }

        var start = FindSequence(output, LegacyHeader);
        if (start >= 0)
        {
            output.RemoveRange(start, LegacyHeader.Length);
            output.InsertRange(start, ReplacementHeader);
        }

        // 什么都没改就原样交回：顺手规整空行也是一次改动，会让合规的仓凭空多一个提交。
        if (stripped == 0 && start < 0)
            return attributesText;

        while (output.Count > 0 && output[^1].Trim().Length == 0)
            output.RemoveAt(output.Count - 1);
        return output.Count == 0 ? string.Empty : string.Join(newline, output) + newline;
    }

    private static int QuotedLength(string raw)
    {
        for (var i = 1; i < raw.Length; i++)
        {
            if (raw[i] == '\\') { i++; continue; }
            if (raw[i] == '"') return i + 1;
        }
        return raw.Length;
    }

    private static int FindSequence(List<string> lines, string[] sequence)
    {
        for (var i = 0; i + sequence.Length <= lines.Count; i++)
        {
            var match = true;
            for (var j = 0; j < sequence.Length && match; j++)
                match = lines[i + j].Trim() == sequence[j];
            if (match)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// 把字节数排成人读的形态。与 <see cref="LargeFileEntry.FormattedSize"/> 同一口径。
    /// </summary>
    public static string FormatBytes(long bytes) => new LargeFileEntry("", bytes).FormattedSize;
}
