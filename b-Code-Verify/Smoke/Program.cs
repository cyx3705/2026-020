using HistoryVulcan.Core;
using HistoryJanus.Smoke;
using HistoryJanus.Smoke.Suites;

// 用法：
//   Smoke.exe                                      依次运行全部默认套件
//   Smoke.exe --suite RepositoryTargets            只运行指定功能套件
//   Smoke.exe --suite GitRules --real-template     向功能套件透传可选参数
//   Smoke.exe --suite ProjectOperations             运行项目操作页规则行为

// 冒烟宿主不运行 WPF 装配点，因此在此显式登记应用身份。
AppIdentity.Use(typeof(HistoryJanus.Git.ProjectService).Assembly);
Environment.CurrentDirectory = SmokeKit.RepoRoot;

var suites = new (string Name, Func<string[], Task> Run)[]
{
    ("VersionProjection", VersionProjectionSuite.RunAsync),
    ("TestArchitecture", TestArchitectureSuite.RunAsync),
    ("GitRules", GitRulesSuite.RunAsync),
    ("LfsRules", LfsRulesSuite.RunAsync),
    ("BranchHistory", BranchHistorySuite.RunAsync),
    ("BranchGraph", BranchGraphSuite.RunAsync),
    ("SubmoduleSafety", SubmoduleSafetySuite.RunAsync),
    ("RepositoryTargets", RepositoryTargetsSuite.RunAsync),
    ("ProjectOperations", ProjectOperationsSuite.RunAsync),
    ("GitHub", GitHubSuite.RunAsync),
};

var selected = ReadSuiteName(args);
if (selected != null
    && !suites.Any(suite => suite.Name.Equals(selected, StringComparison.OrdinalIgnoreCase)))
{
    Console.Error.WriteLine(
        $"未知用例: {selected};可选: {string.Join(", ", suites.Select(suite => suite.Name))}");
    return 2;
}

// 套件之间不共享可变状态(各自建 GUID 临时仓库),因此并行执行;
// 调度、具名超时与耗时汇报都由 SmokeRunner 承担,本文件只保留套件清单。
var pending = suites
    .Where(suite => selected == null || suite.Name.Equals(selected, StringComparison.OrdinalIgnoreCase))
    .ToArray();

var outcomes = await SmokeRunner.RunAllAsync(pending, args);
var failed = SmokeRunner.Report(outcomes);

if (SmokeRunner.AnyTimedOut(outcomes))
{
    // 挂住的套件无法回收,直接结束进程,避免整轮卡在退出等待上。
    Console.Error.Flush();
    Environment.Exit(1);
}

return failed == 0 ? 0 : 1;

static string? ReadSuiteName(string[] args)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals("--suite", StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }

    return null;
}
