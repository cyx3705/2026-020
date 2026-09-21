using HistoryJanus.Git;
using HistoryJanus.Module;
using Xunit;

namespace HistoryJanus.Contracts;

/// <summary>
/// LFS 规则的纯函数部分（5.11.0，DEC-032）。这里钉的是「写出来的那一行长什么样」——
/// 写错一个字符的后果不报错：不带前导斜杠会匹配任意层级的同名文件（2026-09 的裂图），
/// oversize 块排在 !z-*/ 豁免之前会被整个盖掉，去掉 filter=lfs 时顺手丢了 -text
/// 会让 text=auto 把没有 NUL 字节的二进制当文本改换行。
/// </summary>
public sealed class LfsPolicyContractTests
{
    [Fact]
    public void RulesAreAnchoredExactPaths()
    {
        Assert.Equal("/data/big.bin filter=lfs diff=lfs merge=lfs -text", LfsPolicy.AttributesLine("data/big.bin"));
        Assert.Equal("/data/big.bin filter=lfs diff=lfs merge=lfs -text", LfsPolicy.AttributesLine(@".\data\big.bin"));
        Assert.Equal("/data/big.bin", LfsPolicy.IgnoreLine("data/big.bin"));
    }

    [Fact]
    public void SpacesQuotesAndGlobCharactersRoundTrip()
    {
        foreach (var path in new[] { "b-2D/减震器 (改2).dwg", "a/[x]*.bin", "a/b\"c.bin", "a/trailing " })
        {
            var attributes = LfsPolicy.WriteLfsBlock("", [path]);
            Assert.Equal([LfsPolicy.Normalize(path)], LfsPolicy.ReadLfsPaths(attributes));
            var ignore = LfsPolicy.WriteIgnoreBlock("", [path]);
            Assert.Equal([LfsPolicy.Normalize(path)], LfsPolicy.ReadIgnoredPaths(ignore));
        }

        Assert.StartsWith("\"/b-2D/减震器 (改2).dwg\"", LfsPolicy.AttributesLine("b-2D/减震器 (改2).dwg"));
        Assert.Equal("/a/\\[x\\]\\*.bin", LfsPolicy.IgnoreLine("a/[x]*.bin"));
    }

    [Fact]
    public void TheOversizeBlockAlwaysEndsTheIgnoreFile()
    {
        var managed = "# HistoryJanus managed begin\nbin/\n!z-*/\n!z-*/**\n# HistoryJanus managed end\n";
        var first = LfsPolicy.WriteIgnoreBlock(managed, ["z-x/big.bin"]);
        // 托管块之后有人又追加了内容：再写一次，oversize 块仍要挪回最后。
        var appended = first + "# HistoryJanus managed begin2\n!keep\n";
        var second = LfsPolicy.WriteIgnoreBlock(appended, ["a.bin"]);

        Assert.EndsWith(LfsPolicy.IgnoreEnd + "\n", second);
        Assert.Equal(1, Count(second, LfsPolicy.IgnoreBegin));
        Assert.Equal(["a.bin"], LfsPolicy.ReadIgnoredPaths(second));
        Assert.DoesNotContain(LfsPolicy.IgnoreBegin, LfsPolicy.WriteIgnoreBlock(second, []));
    }

    [Fact]
    public void RewritingTheLfsBlockIsIdempotentAndKeepsItsPlace()
    {
        var text = "* text=auto\n\n" + LfsPolicy.WriteLfsBlock("", ["b.bin"]) + "\nz-Publish/** binary\n";
        var once = LfsPolicy.WriteLfsBlock(text, ["b.bin", "a.bin"]);
        Assert.Equal(once, LfsPolicy.WriteLfsBlock(once, ["a.bin", "b.bin"]));
        Assert.True(once.IndexOf(LfsPolicy.AttributesBegin, StringComparison.Ordinal)
                    < once.IndexOf("z-Publish/** binary", StringComparison.Ordinal));
        Assert.Equal(0, LfsPolicy.CountForeignLfsRules(once));
    }

    [Fact]
    public void AnythingOutsideTheBlockIsAViolationAndStrippingKeepsTheBinaryGuard()
    {
        var text = "* text=auto\n" +
                   "*.dll filter=lfs diff=lfs merge=lfs -text\n" +
                   "Logo.png filter=lfs diff=lfs merge=lfs -text\n" +
                   "\"a b/c.dwg\" filter=lfs diff=lfs merge=lfs -text\n" +
                   "*.zip filter=lfs\n" +
                   LfsPolicy.WriteLfsBlock("", ["big.iso"]);
        Assert.Equal(4, LfsPolicy.CountForeignLfsRules(text));

        var stripped = LfsPolicy.StripForeignLfsRules(text, out var count);
        Assert.Equal(4, count);
        Assert.Equal(0, LfsPolicy.CountForeignLfsRules(stripped));
        Assert.Contains("*.dll -text\n", stripped);
        Assert.Contains("Logo.png -text\n", stripped);
        Assert.Contains("\"a b/c.dwg\" -text\n", stripped);
        Assert.DoesNotContain("*.zip", stripped);
        Assert.Equal(["big.iso"], LfsPolicy.ReadLfsPaths(stripped));
    }

    [Fact]
    public void TheLegacyConservativeHeaderIsReplaced()
    {
        var legacy = string.Join("\n",
            "# 极保守 LFS 策略：不按扩展名批量套 LFS。",
            "# 旧的按扩展名通配（*.asm / *.baml 这类文本也在内）是 11GB LFS 占用",
            "# 与推送被 GitHub pre-receive 拒收的根因。",
            "# 下面只逐条列出**已经是 LFS 指针**的现存文件，保证这些指针不失效；",
            "# 新文件一律不自动进 LFS——只有单个文件超过 GitHub 100MB 硬限时，",
            "# 才由提交链路逐个征求人工同意后按精确路径追加。",
            "* text=auto",
            "a.png filter=lfs diff=lfs merge=lfs -text") + "\n";
        var stripped = LfsPolicy.StripForeignLfsRules(legacy, out _);
        Assert.DoesNotContain("极保守", stripped);
        Assert.Contains("不到 100MB 的文件一律不走 LFS", stripped);
    }

    /// <summary>「按格式」= 模式里有未转义的通配；精确路径（哪怕不带前导斜杠）不算。</summary>
    [Fact]
    public void FormatOnlyStripsWildcardRulesAndLeavesExactPaths()
    {
        Assert.True(LfsPolicy.IsFormatPattern("*.dll"));
        Assert.True(LfsPolicy.IsFormatPattern("z-Publish/**/*.dll"));
        Assert.True(LfsPolicy.IsFormatPattern("a/file?.bin"));
        Assert.False(LfsPolicy.IsFormatPattern("Logo.png"));
        Assert.False(LfsPolicy.IsFormatPattern("b-Module/0000.asm"));
        Assert.False(LfsPolicy.IsFormatPattern("/a/\\[x\\].bin"));

        var text = "# 极保守 LFS 策略：不按扩展名批量套 LFS。\n" +
                   "*.dll filter=lfs diff=lfs merge=lfs -text\n" +
                   "b-Module/0000.asm filter=lfs diff=lfs merge=lfs -text\n";
        var stripped = LfsPolicy.StripForeignLfsRules(text, out var count, formatOnly: true);
        Assert.Equal(1, count);
        Assert.Contains("*.dll -text\n", stripped);
        Assert.Contains("b-Module/0000.asm filter=lfs", stripped);
        Assert.Contains("极保守", stripped);
    }

    /// <summary>合规的仓不能因为「顺手规整空行」凭空多一个修复提交。</summary>
    [Fact]
    public void ACompliantFileComesBackByteIdentical()
    {
        var text = "* text=auto\r\n*.dll -text\r\n\r\n\r\n";
        Assert.Same(text, LfsPolicy.StripForeignLfsRules(text, out var count));
        Assert.Equal(0, count);
        Assert.Same(text, LfsPolicy.WriteLfsBlock(text, []));
        Assert.Same(text, LfsPolicy.WriteIgnoreBlock(text, []));
    }

    [Fact]
    public void OnlyTopLevelZDirectoriesAreSnapshots()
    {
        Assert.True(LfsPolicy.IsSnapshotPath("z-Publish/a.dll"));
        Assert.True(LfsPolicy.IsSnapshotPath("Z-HistoryVulcan/host/a.dll"));
        Assert.False(LfsPolicy.IsSnapshotPath("z-file.bin"));
        Assert.False(LfsPolicy.IsSnapshotPath("b-Code/z-Publish/a.dll"));
    }

    /// <summary>
    /// 「操作」格：显示当前决定加符号，点一下切到另一个去向（5.12.0）。
    /// 未决定先落到 LFS 指针——它保留文件，误点的代价最小。已被入库规则排除的文件不列。
    /// </summary>
    [Fact]
    public void TheOperationCellCyclesBetweenTheTwoDestinations()
    {
        var report = new LfsInspection("p", true,
        [
            new LfsFileRow("a.bin", 1, "", "未跟踪", "未决定", ""),
            new LfsFileRow("b.bin", 1, "", "LFS 指针", "LFS 指针", ""),
            new LfsFileRow("c.bin", 1, "", "普通入库", "不纳入 git", ""),
            new LfsFileRow("bin/d.bin", 1, "", "已被排除", "—", ""),
        ], 1, 1, 4, 1, 1, 1, 0, 0, 0);
        var rows = HistoryJanusUiCommands.UiLfsProjection.Files((true, "", report));

        Assert.Equal(new[] { "a.bin", "b.bin", "c.bin" }, rows.Select(r => r["path"]).ToArray());
        Assert.Equal(new[] { "未决定 ○", "LFS 指针 ●", "不纳入 ✕" }, rows.Select(r => r["op"]).ToArray());
        Assert.Equal(new[] { "lfs", "ignore", "lfs" }, rows.Select(r => r["next"]).ToArray());

        Assert.Equal("✓", HistoryJanusUiCommands.UiLfsProjection.Stat((true, "", report), "compliance")[0]["value"]);
        var dirty = report with { SmallPointerCount = 2 };
        Assert.Equal("✗", HistoryJanusUiCommands.UiLfsProjection.Stat((true, "", dirty), "compliance")[0]["value"]);
        Assert.Equal("—", HistoryJanusUiCommands.UiLfsProjection.Stat((false, "x", null), "bytes")[0]["value"]);
    }

    private static int Count(string text, string token)
        => (text.Length - text.Replace(token, "", StringComparison.Ordinal).Length) / token.Length;
}
