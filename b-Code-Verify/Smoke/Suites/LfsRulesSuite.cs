using HistoryJanus.Git;
using static HistoryJanus.Smoke.SmokeKit;

namespace HistoryJanus.Smoke.Suites;

/// <summary>
/// LFS 规则（5.11.0，DEC-032）：不到 100MB 的文件一律不走 LFS；≥100MB 的由人定去向，
/// 下次提交生效，历史不改写。全部用真实 git + git-lfs 仓库断言——这些规则出错时的症状
/// （一张图存成 130 字节指针、提交里悄悄混进 LFS 通配）在字符串层面是看不出来的。
/// </summary>
internal static class LfsRulesSuite
{
    private const string Project = "2026-250-Lfs";

    public static async Task RunAsync(string[] args)
    {
        var temp = TemporaryDirectory("lfsrules");
        try
        {
            await TestRepairConvertsSmallPointersAsync(temp);
            await TestFormatOnlyRepairKeepsExactRulesAsync(temp);
            await TestCommitRefusesForeignLfsRulesAsync(temp);
            await TestDecisionsApplyOnNextCommitAsync(temp);
        }
        finally
        {
            DeleteTree(temp);
        }
    }

    /// <summary>
    /// 旧仓：按扩展名通配走 LFS，一个 1KB 的文件存成了指针。修复后它以真实内容入库，
    /// 通配行只剩 -text（二进制保护不能丢），而且是一次本地提交、没有改写历史。
    /// </summary>
    private static async Task TestRepairConvertsSmallPointersAsync(string temp)
    {
        var (root, project, service, _) = await NewLibraryAsync(temp, "repair");
        await File.WriteAllTextAsync(Path.Combine(project, ".gitattributes"),
            "* text=auto\n*.bin filter=lfs diff=lfs merge=lfs -text\n");
        await File.WriteAllBytesAsync(Path.Combine(project, "small.bin"), Enumerable.Repeat((byte)7, 1024).ToArray());
        Run(project, ["add", "-A"]);
        Run(project, ["commit", "-m", "legacy lfs"]);
        True(long.Parse(Run(project, ["cat-file", "-s", "HEAD:small.bin"]).Trim()) < 300,
            "precondition: the small file is stored as an LFS pointer");
        var before = Run(project, ["rev-list", "--count", "HEAD"]).Trim();

        var dry = await service.RepairAsync(Project, commit: true, dryRun: true, progress: null);
        True(dry.Success && dry.Report is { DryRun: true, Converted: 1 }, $"a dry run plans one conversion: {dry.Message}");
        True(long.Parse(Run(project, ["cat-file", "-s", "HEAD:small.bin"]).Trim()) < 300,
            "a dry run changes nothing");

        var repaired = await service.RepairAsync(Project, commit: true, dryRun: false, progress: null);
        True(repaired.Success, $"repair succeeds: {repaired.Message}");
        Equal("1024", Run(project, ["cat-file", "-s", "HEAD:small.bin"]).Trim(),
            "the small file is committed with its real content");
        var attributes = await File.ReadAllTextAsync(Path.Combine(project, ".gitattributes"));
        True(!attributes.Contains("filter=lfs", StringComparison.Ordinal), "no LFS rule is left");
        Contains(attributes, "*.bin -text", "the binary guard survives the strip");
        Equal((int.Parse(before) + 1).ToString(), Run(project, ["rev-list", "--count", "HEAD"]).Trim(),
            "the repair adds one commit on top; history is not rewritten");
        True((await service.CheckPolicyAsync(project)).Success, "the repaired repo passes the policy gate");

        DeleteTree(root);
    }

    /// <summary>
    /// 只删按格式的规则：<c>*.bin</c> 删掉，被它覆盖的小指针同批转回（否则克隆拿到的是指针文本）；
    /// 精确路径那一条连同它的指针原样留着，等全量修复。
    /// </summary>
    private static async Task TestFormatOnlyRepairKeepsExactRulesAsync(string temp)
    {
        var (root, project, service, _) = await NewLibraryAsync(temp, "format");
        await File.WriteAllTextAsync(Path.Combine(project, ".gitattributes"),
            "*.bin filter=lfs diff=lfs merge=lfs -text\nkeep.dat filter=lfs diff=lfs merge=lfs -text\n");
        await File.WriteAllBytesAsync(Path.Combine(project, "small.bin"), Enumerable.Repeat((byte)7, 1024).ToArray());
        await File.WriteAllBytesAsync(Path.Combine(project, "keep.dat"), Enumerable.Repeat((byte)9, 2048).ToArray());
        Run(project, ["add", "-A"]);
        Run(project, ["commit", "-m", "legacy lfs"]);

        var repaired = await service.RepairAsync(Project, commit: true, dryRun: false, progress: null, formatOnly: true);
        True(repaired.Success, $"format-only repair succeeds: {repaired.Message}");
        Equal("1024", Run(project, ["cat-file", "-s", "HEAD:small.bin"]).Trim(),
            "a file covered only by the wildcard is committed with its real content");
        True(long.Parse(Run(project, ["cat-file", "-s", "HEAD:keep.dat"]).Trim()) < 300,
            "a file under an exact-path rule stays a pointer");
        var attributes = await File.ReadAllTextAsync(Path.Combine(project, ".gitattributes"));
        Contains(attributes, "*.bin -text", "the wildcard rule loses only its LFS attributes");
        Contains(attributes, "keep.dat filter=lfs", "the exact-path rule is untouched");

        DeleteTree(root);
    }

    /// <summary>托管块之外有 filter=lfs 时，提交链路拒绝并指向修复命令。</summary>
    private static async Task TestCommitRefusesForeignLfsRulesAsync(string temp)
    {
        var (root, project, service, projects) = await NewLibraryAsync(temp, "guard");
        await File.WriteAllTextAsync(Path.Combine(project, ".gitattributes"),
            "Logo.png filter=lfs diff=lfs merge=lfs -text\n");
        await File.WriteAllTextAsync(Path.Combine(project, "note.txt"), "x");

        var report = await projects.CommitAsync(Project, "should be refused", null);
        True(report.Outcome == CommitOutcome.Failed, $"the commit is refused: {report.Message}");
        Contains(report.Message, "janus.gitrule.lfsrepair", "the refusal names the repair command");

        DeleteTree(root);
    }

    /// <summary>
    /// ≥100MB 的文件：不到 100MB 的拒绝定去向；定为 LFS 后下次提交它是指针；
    /// 改为不纳入 git 后下次提交移出索引、本地文件还在；z-* 里的不能选不纳入 git。
    /// </summary>
    private static async Task TestDecisionsApplyOnNextCommitAsync(string temp)
    {
        var (root, project, service, projects) = await NewLibraryAsync(temp, "decide");
        await File.WriteAllTextAsync(Path.Combine(project, "tiny.dat"), "tiny");
        var big = Path.Combine(project, "data", "big.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(big)!);
        await using (var stream = File.Create(big))
            stream.SetLength(LfsPolicy.ThresholdBytes + 1);
        var snapshot = Path.Combine(project, "z-Demo", "big.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(snapshot)!);
        await using (var stream = File.Create(snapshot))
            stream.SetLength(LfsPolicy.ThresholdBytes + 1);

        True(!(await service.SetDecisionAsync(Project, "tiny.dat", LfsDecision.Lfs)).Success,
            "a file under 100MB can never be put into LFS");
        True(!(await service.SetDecisionAsync(Project, "z-Demo/big.dat", LfsDecision.Ignore)).Success,
            "a formal snapshot file cannot be dropped from git");
        True((await service.SetDecisionAsync(Project, "z-Demo/big.dat", LfsDecision.Lfs)).Success,
            "a snapshot file may go to LFS");

        var toLfs = await service.SetDecisionAsync(Project, "data/big.dat", LfsDecision.Lfs);
        True(toLfs.Success, $"an oversize file can be put into LFS: {toLfs.Message}");
        var attributes = await File.ReadAllTextAsync(Path.Combine(project, ".gitattributes"));
        Contains(attributes, "/data/big.dat filter=lfs", "the rule is an anchored exact path");

        var first = await projects.CommitAsync(Project, "lfs decision", null);
        True(first.Outcome == CommitOutcome.Success, $"the commit applies the decision: {first.Message}");
        True(long.Parse(Run(project, ["cat-file", "-s", "HEAD:data/big.dat"]).Trim()) < 300,
            "the oversize file is committed as a pointer");

        var toIgnore = await service.SetDecisionAsync(Project, "data/big.dat", LfsDecision.Ignore);
        True(toIgnore.Success, $"the decision can be changed: {toIgnore.Message}");
        var ignore = await File.ReadAllTextAsync(Path.Combine(project, ".gitignore"));
        True(ignore.TrimEnd().EndsWith(LfsPolicy.IgnoreEnd, StringComparison.Ordinal),
            "the oversize block sits at the very end of .gitignore");

        var second = await projects.CommitAsync(Project, "ignore decision", null);
        True(second.Outcome == CommitOutcome.Success, $"the next commit applies it: {second.Message}");
        True(!Run(project, ["ls-files"]).Contains("data/big.dat", StringComparison.Ordinal),
            "the file leaves the index");
        True(File.Exists(big), "the local file is kept");
        Contains(Run(project, ["ls-files"]), "tiny.dat", "ordinary small files are committed normally");

        DeleteTree(root);
    }

    private static async Task<(string Root, string Project, LfsRuleService Service, ProjectService Projects)>
        NewLibraryAsync(string temp, string name)
    {
        var root = Path.Combine(temp, name);
        var project = Path.Combine(root, Project);
        Directory.CreateDirectory(root);
        await InitStandaloneRepo(project, "Lfs Smoke", "lfs@example.invalid");
        Run(project, ["lfs", "install", "--local"]);
        await CommitFile(project, "README.md", "seed\n", "seed");

        var settings = new MemorySettings();
        BindLibrary(settings, root);
        var projects = new ProjectService(settings, _ => true, temp);
        var service = new LfsRuleService(projects);
        projects.LfsRules = service;
        return (root, project, service, projects);
    }
}
