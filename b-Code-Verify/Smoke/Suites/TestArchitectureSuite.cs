using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using HistoryJanus.Git;
using static HistoryJanus.Smoke.SmokeKit;

namespace HistoryJanus.Smoke.Suites;

/// <summary>Smoke 套件的功能命名、文件体积、运行隔离与单宿主结构。</summary>
internal static class TestArchitectureSuite
{
    private const int MaximumProductFileLines = 700;
    private const int MaximumSuiteFileLines = 550;

    public static async Task RunAsync(string[] args)
    {
        True(Directory.Exists(VerifyRoot), "verification has a root-level b-Code component");
        True(!Directory.Exists(Path.Combine(RepoRoot, "tests")),
            "product component contains no nested test tree");
        var separator = Path.DirectorySeparatorChar;
        var productFiles = Directory.EnumerateFiles(RepoRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase)
                           && !path.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        True(productFiles.Length > 0, "test architecture discovers product sources");
        foreach (var path in productFiles)
        {
            var lines = File.ReadLines(path).Count();
            True(lines <= MaximumProductFileLines,
                $"product source stays within {MaximumProductFileLines} lines: " +
                $"{Path.GetRelativePath(RepoRoot, path)} ({lines})");
        }

        VerifyGitHubMerge(separator);
        VerifyV3HostBoundary();
        VerifyMergedOverviewBoundary(separator);
        VerifyEmbeddedHistoryBoundary(separator);
        VerifyThemeBoundary();
        VerifyGitExecutionBoundary();

        var smokeRoot = Path.Combine(VerifyRoot, "Smoke");
        var suitesRoot = Path.Combine(smokeRoot, "Suites");
        var suiteFiles = Directory.EnumerateFiles(suitesRoot, "*.cs").ToArray();
        True(suiteFiles.Length > 0, "test architecture discovers suite sources");

        foreach (var path in suiteFiles)
        {
            var name = Path.GetFileName(path);
            var source = File.ReadAllText(path);
            var lines = File.ReadLines(path).Count();
            True(!Regex.IsMatch(name, @"^V\d", RegexOptions.CultureInvariant),
                $"test suite uses a functional filename: {name}");
            True(lines <= MaximumSuiteFileLines,
                $"test suite source stays within {MaximumSuiteFileLines} lines: {name} ({lines})");
            True(!source.Contains("Environment.Current" + "Directory =", StringComparison.Ordinal),
                $"test suite does not mutate process working directory: {name}");
            True(!source.Contains(".tmp-" + "v", StringComparison.OrdinalIgnoreCase),
                $"test suite does not create version-named source-tree temp data: {name}");
            True(!Regex.IsMatch(source,
                    @"Console\.WriteLine\([^\r\n]*Smoke:\s*PASS",
                    RegexOptions.CultureInvariant),
                $"test suite leaves PASS output to the runner: {name}");
        }

        var program = File.ReadAllText(Path.Combine(smokeRoot, "Program.cs"));
        var registered = Regex.Matches(program,
                "\\(\\\"(?<name>[A-Za-z][A-Za-z0-9]+)\\\",",
                RegexOptions.CultureInvariant)
            .Select(match => match.Groups["name"].Value)
            .ToArray();
        string[] expected =
        [
            "VersionProjection", "TestArchitecture", "GitRules", "BranchHistory",
            "BranchGraph", "SubmoduleSafety", "RepositoryTargets", "ProjectOperations", "GitHub",
        ];
        True(registered.SequenceEqual(expected),
            "test runner registers the reviewed functional suite order");
        Equal(registered.Length, registered.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            "test runner suite names are unique");
        True(!Regex.IsMatch(program, "\\\"V\\d", RegexOptions.CultureInvariant),
            "test runner exposes no version-named suite");

        // 执行效率与挂死可见性是合同要求(QA-005/QA-006)，不是实现细节：
        // 运行器必须并行调度并对每个套件设墙钟上限，否则整轮会退回串行或静默挂住。
        var runner = File.ReadAllText(Path.Combine(smokeRoot, "SmokeRunner.cs"));
        Contains(runner, "Task.WhenAll",
            "test runner schedules independent suites concurrently");
        Contains(runner, "SuiteTimeout",
            "test runner bounds every suite with a wall-clock timeout");
        Contains(runner, "TIMEOUT",
            "test runner names the suite that hangs instead of stalling silently");
        True(!Regex.IsMatch(program, @"foreach\s*\([^)]*\bsuites\b", RegexOptions.CultureInvariant),
            "test runner does not fall back to a sequential suite loop");

        var project = XDocument.Load(Path.Combine(smokeRoot, "Smoke.csproj"));
        Equal(1, project.Descendants("ProjectReference").Count(),
            "test architecture uses the single module product reference");
        Contains(project.Descendants("ProjectReference").Single().Attribute("Include")?.Value ?? "",
            "HistoryJanus.Module.csproj",
            "test architecture references the module project");
        var kit = File.ReadAllText(Path.Combine(smokeRoot, "SmokeKit.cs"));
        Contains(kit, "Path.GetTempPath()",
            "test architecture centralizes temporary data outside the source tree");

        await VerifyGitRunnerFailureAsync();
        await VerifyGitRunnerCancellationAsync();
    }

    private static void VerifyV3HostBoundary()
    {
        var businessPath = Path.Combine(RepoRoot, "StudioBusinessComposition.cs");
        var business = File.ReadAllText(businessPath);
        foreach (var prohibited in new[]
                 {
                     "ServiceHost", "ModuleHost", "McpGateway", "WebGateway",
                     "ShellServiceClient", "ShellWindow",
                 })
        {
            True(!business.Contains(prohibited, StringComparison.Ordinal),
                $"V3 business composition does not own host capability: {prohibited}");
        }

        foreach (var injected in new[]
                 {
                     "CommandRegistry registry", "CommandBus bus", "ISettingsService settings",
                     "IShellLog log", "string dataDirectory", "string commandSource",
                 })
        {
            Contains(business, injected,
                $"V3 business composition receives host dependency: {injected}");
        }

        True(!File.Exists(Path.Combine(RepoRoot, "Studio.csproj")),
            "module boundary: legacy Studio project is absent");
        True(!File.Exists(Path.Combine(RepoRoot, "Program.cs")),
            "module boundary: legacy process entry is absent");
        True(!Directory.Exists(Path.Combine(RepoRoot, "Connection")),
            "module boundary: legacy connection tree is absent");
        True(!Directory.Exists(Path.Combine(RepoRoot, "Service")),
            "module boundary: legacy service tree is absent");
    }

    private static void VerifyGitExecutionBoundary()
    {
        var gitRoot = Path.Combine(RepoRoot, "Git");
        var runner = File.ReadAllText(Path.Combine(gitRoot, "GitRunner.cs"));
        True(!runner.Contains("timeoutSeconds", StringComparison.Ordinal)
             && !runner.Contains("CancellationTokenSource(TimeSpan", StringComparison.Ordinal)
             && !runner.Contains("git 命令超时", StringComparison.Ordinal),
            "product GitRunner delegates timeout decisions to Git and Git LFS");
        Contains(runner, "WaitForExitAsync(cancellation)",
            "product GitRunner still responds to caller cancellation");
        Contains(runner, "Kill(entireProcessTree: true)",
            "caller cancellation still reclaims the complete Git process tree");

        foreach (var name in new[] { "ProjectService.Commit.cs", "BranchHistoryService.cs" })
        {
            var source = File.ReadAllText(Path.Combine(gitRoot, name));
            True(!source.Contains("timeoutSeconds:", StringComparison.Ordinal),
                $"product Git call sites carry no application wall-clock timeout: {name}");
        }

        var githubRunner = File.ReadAllText(Path.Combine(RepoRoot, "GitHub", "ToolProcessRunner.cs"));
        Contains(githubRunner, "timeoutSeconds",
            "GitHub diagnostics retain their explicit user-facing timeout");
        var smokeRunner = File.ReadAllText(Path.Combine(VerifyRoot, "Smoke", "SmokeRunner.cs"));
        Contains(smokeRunner, "SuiteTimeout",
            "test suites retain their independent hang-detection timeout");
    }

    private static async Task VerifyGitRunnerCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var watch = Stopwatch.StartNew();
        var result = await GitRunner.RunAsync(RepoRoot,
            [
                "-c",
                "alias.januswait=!powershell -NoProfile -NonInteractive -Command Start-Sleep -Seconds 30",
                "januswait",
            ],
            cancellation.Token);
        watch.Stop();

        True(!result.Success && result.Output.Contains("git 命令已取消", StringComparison.Ordinal),
            "caller cancellation remains distinguishable from a Git failure");
        True(watch.Elapsed < TimeSpan.FromSeconds(5),
            "caller cancellation promptly reclaims a long-running Git process tree");
    }

    private static async Task VerifyGitRunnerFailureAsync()
    {
        const string missingCommand = "janus-command-that-does-not-exist";
        var result = await GitRunner.RunAsync(RepoRoot, [missingCommand]);

        True(result.ExitCode != 0 && !result.Success,
            "a real Git failure retains its nonzero exit code");
        True(result.Output.Contains(missingCommand, StringComparison.Ordinal),
            "a real Git failure retains Git's own diagnostic output");
    }

    private static void VerifyGitHubMerge(char separator)
    {
        True(File.Exists(Path.Combine(VerifyRoot, "Smoke", "Suites", "GitHubSuite.cs")),
            "merged github: functional Smoke suite exists");

        string[] mergedFiles =
        [
            "GitHub/GitHubConnectionModels.cs",
            "GitHub/GitHubConnectionService.cs",
            "GitHub/GitHubRedactor.cs",
            "GitHub/ToolProcessRunner.cs",
            "GitHub/GitHubCommands.cs",
            "Views/GitHubConnectionView.xaml",
            "Views/GitHubConnectionView.xaml.cs",
        ];
        foreach (var relativePath in mergedFiles)
            True(File.Exists(Path.Combine(RepoRoot, relativePath.Replace('/', separator))),
                $"merged github: source is present: {relativePath}");

        // 独立模块时代的外壳与旧路径解析不得复活；GitHub 指向选中项目仓
        string[] removedIdentifiers =
        [
            "RepositoryPathResolver",
            "\"github.account\"",
            "GitHubRuntime",
        ];
        var productionFiles = Directory.EnumerateFiles(RepoRoot, "*", SearchOption.AllDirectories)
            .Where(path => new[] { ".cs", ".xaml", ".csproj" }
                .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase)
                           && !path.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase));
        foreach (var path in productionFiles)
        {
            var source = File.ReadAllText(path);
            foreach (var identifier in removedIdentifiers)
                True(!source.Contains(identifier, StringComparison.Ordinal),
                    $"merged github: standalone shell stays removed: {identifier} in " +
                    Path.GetRelativePath(RepoRoot, path));
        }

        var commands = File.ReadAllText(Path.Combine(RepoRoot, "GitHub", "GitHubCommands.cs"));
        foreach (var retained in new[] { "\"janus.github.status\"", "\"janus.github.accounts\"", "\"janus.github.test\"" })
        {
            Contains(commands, retained,
                $"merged github: readonly command stays registered: {retained}");
        }
        foreach (var mutation in new[] { "janus.github.login", "janus.github.logout", "janus.github.identity", "janus.github.remote" })
            Contains(commands, $"\"{mutation}\"",
                $"merged github: mutation is registered through the command bus: {mutation}");

        var composition = File.ReadAllText(Path.Combine(RepoRoot, "StudioBusinessComposition.cs"));
        Contains(composition, "GitHubCommands.RegisterAll",
            "merged github: commands register through the module composition");
        Contains(composition, "ResolveGitHubRepository",
            "merged github: repository path is the selected project under proj.libraryroot");
        Contains(composition, "GraphCommands.RegisterAll",
            "graph commands register through the module composition");
    }

    private static void VerifyMergedOverviewBoundary(char separator)
    {
        foreach (var removed in new[]
                 {
                     "Views/MetaView.xaml", "Views/MetaView.xaml.cs",
                     "Views/BranchTreeView.xaml", "Views/BranchTreeView.xaml.cs",
                     "Views/BranchTreeItem.cs",
                 })
        {
            True(!File.Exists(Path.Combine(RepoRoot, removed.Replace('/', separator))),
                $"merged overview: standalone view is removed: {removed}");
        }

        foreach (var retained in new[]
                 {
                     "Git/BranchTreeService.cs", "Git/BranchTreeNode.cs", "Git/ProjectService.Meta.cs",
                 })
        {
            True(File.Exists(Path.Combine(RepoRoot, retained.Replace('/', separator))),
                $"merged overview: background service stays: {retained}");
        }

        var module = File.ReadAllText(Path.Combine(RepoRoot, "Module", "HistoryJanusUiModule.cs"));
        var windowIds = Regex.Matches(module, @"Id = ""(?<id>[a-z]+)""", RegexOptions.CultureInvariant)
            .Select(match => match.Groups["id"].Value)
            .ToArray();
        // DEC-008：github 窗口退役，GitHub 面板是 projops 的第三个分段。
        True(windowIds.SequenceEqual(new[] { "overview", "graph", "projops" }),
            "page consolidation: module registers overview, graph, and projops");
        Contains(
            File.ReadAllText(Path.Combine(RepoRoot, "Views", "ProjectOperationsView.xaml.cs")),
            "GitHubPanel.Content = new GitHubConnectionView",
            "page consolidation: GitHub governance is embedded in the project operations page");

        var commands = File.ReadAllText(Path.Combine(RepoRoot, "Git", "ProjectCommands.cs"));
        foreach (var retained in new[] { "\"janus.proj.tree\"", "\"janus.proj.metas\"", "\"janus.proj.metaopen\"" })
        {
            Contains(commands, retained,
                $"merged overview: background command stays registered: {retained}");
        }
    }

    private static void VerifyEmbeddedHistoryBoundary(char separator)
    {
        foreach (var retained in new[]
                 {
                     "Views/BranchHistoryView.xaml", "Views/BranchHistoryView.xaml.cs",
                 })
        {
            True(File.Exists(Path.Combine(RepoRoot, retained.Replace('/', separator))),
                $"embedded history: view component stays: {retained}");
        }

        var projectOperations = File.ReadAllText(
            Path.Combine(RepoRoot, "Views", "ProjectOperationsView.xaml.cs"));
        Contains(projectOperations, "new BranchHistoryView(",
            "embedded history: project operations hosts the branch history component");

        var commands = File.ReadAllText(Path.Combine(RepoRoot, "Git", "BranchHistoryCommands.cs"));
        foreach (var retained in new[]
                 {
                     "\"janus.history.list\"", "\"janus.history.show\"", "\"janus.history.diff\"",
                     "\"janus.history.rollback\"", "\"janus.history.reset\"", "\"janus.history.forcepush\"",
                 })
        {
            Contains(commands, retained,
                $"embedded history: history command stays registered: {retained}");
        }
    }

    private static void VerifyThemeBoundary()
    {
        var viewsRoot = Path.Combine(RepoRoot, "Views");
        var literalColor = new Regex(
            "(?:Background|Foreground|BorderBrush|Fill|Stroke|Value)\\s*=\\s*\\\"(?:White|Black|Gray|#[0-9A-Fa-f]+)\\\"",
            RegexOptions.CultureInvariant);
        var views = Directory.EnumerateFiles(viewsRoot, "*.xaml", SearchOption.TopDirectoryOnly)
            .ToArray();
        True(views.Length > 0, "theme governance discovers module XAML views");
        foreach (var path in views)
        {
            var source = File.ReadAllText(path);
            True(!literalColor.IsMatch(source),
                $"view uses HistoryVulcan dynamic color tokens: {Path.GetFileName(path)}");
        }

        var primaryViews = new[]
        {
            "OverviewView.xaml", "GraphView.xaml", "BranchHistoryView.xaml", "ProjectOperationsView.xaml",
            "GitHubConnectionView.xaml",
        };
        foreach (var name in primaryViews)
        {
            var source = File.ReadAllText(Path.Combine(viewsRoot, name));
            Contains(source, "Background=\"{DynamicResource Aurora.Brush.Surface}\"",
                $"theme governance: {name} uses the HistoryVulcan surface token");
            Contains(source, "Aurora.Brush.TextPrimary",
                $"theme governance: {name} uses the host primary text token");
        }

        foreach (var name in new[]
                 {
                     "OverviewView.xaml", "GraphView.xaml", "BranchHistoryView.xaml", "ProjectOperationsView.xaml",
                     "GitHubConnectionView.xaml",
                 })
        {
            var source = File.ReadAllText(Path.Combine(viewsRoot, name));
            True(source.Contains("Aurora.Brush.Surface}", StringComparison.Ordinal)
                 && source.Contains("Aurora.Brush.Hairline}", StringComparison.Ordinal),
                $"theme governance: {name} content controls use surface and hairline tokens");
        }

        var projectOperations = File.ReadAllText(Path.Combine(viewsRoot, "ProjectOperationsView.xaml"));
        foreach (var token in new[]
                  {
                      "Aurora.Brush.SurfaceAlt", "Aurora.Brush.ControlBorder",
                      "Aurora.Brush.SurfaceHover", "Aurora.Brush.AccentSoft", "Aurora.Brush.Accent",
                      "Aurora.Brush.TextDisabled", "Aurora.Brush.Hairline",
                  })
        {
            Contains(projectOperations, token,
                $"theme governance: project operations uses {token}");
        }
        Contains(projectOperations,
            "Foreground=\"{TemplateBinding Foreground}\"",
            "theme governance: operation segment text inherits the host foreground");
    }
}
