using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using HistoryJanus.Git;
using static HistoryJanus.Smoke.SmokeKit;

namespace HistoryJanus.Smoke.Suites;

/// <summary>验证 Janus 模块版本、HistoryVulcan 宿主合同和模块发布边界。</summary>
internal static class VersionProjectionSuite
{
    public static async Task RunAsync(string[] args)
    {
        var studioRoot = Path.Combine(ParentDir, "b-Code-Studio");
        var studioVersion = ReadSingleVersion(
            Path.Combine(studioRoot, "JanusVersion.props"), "HistoryJanusVersion");

        AssertRuntimeAssemblies(studioVersion, [typeof(ProjectService).Assembly]);

        const string moduleProject = "b-Code-Studio/Module/HistoryJanus.Module.csproj";
        var moduleEvaluation = await EvaluateAsync(Path.Combine(ParentDir, moduleProject));
        AssertEvaluatedVersion(moduleEvaluation, studioVersion, moduleProject);
        using (var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(
                   studioRoot, "Module", "module.manifest.json"))))
        {
            Equal(studioVersion, manifest.RootElement.GetProperty("version").GetString(),
                "version projection: module manifest matches the single version source");
        }

        AssertPackageConsumers();
        AssertCurrentSourceAndDocumentation();
        await AssertPublishAreaGovernanceAsync();
        Console.WriteLine($"version projection: Janus module {studioVersion}");
    }

    private static string ReadSingleVersion(string path, string propertyName)
    {
        var values = XDocument.Load(path).Descendants(propertyName)
            .Select(element => element.Value.Trim())
            .Where(value => value.Length != 0)
            .ToArray();
        Equal(1, values.Length, $"version projection: {propertyName} has one source value");
        True(Version.TryParse(values[0], out var parsed) && parsed.Revision < 0,
            $"version projection: {propertyName} is a three-part version");
        return values[0];
    }

    private static void AssertRuntimeAssemblies(string expected, IEnumerable<Assembly> assemblies)
    {
        var expectedAssemblyVersion = Version.Parse($"{expected}.0");
        foreach (var assembly in assemblies.Distinct())
        {
            var name = assembly.GetName().Name ?? "<unknown>";
            Equal(expectedAssemblyVersion, assembly.GetName().Version,
                $"version projection: {name} assembly version");
            Equal($"{expected}.0",
                assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version,
                $"version projection: {name} file version");
            Equal(expected,
                assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                $"version projection: {name} informational version");
        }
    }

    private static void AssertEvaluatedVersion(JsonElement evaluation, string expected, string project)
    {
        Equal(expected, Property(evaluation, "HistoryJanusVersion"),
            $"version projection: {project} source");
        Equal(expected, Property(evaluation, "VersionPrefix"),
            $"version projection: {project} prefix");
        Equal(expected, Property(evaluation, "Version"),
            $"version projection: {project} version");
        Equal($"{expected}.0", Property(evaluation, "AssemblyVersion"),
            $"version projection: {project} assembly version");
        Equal($"{expected}.0", Property(evaluation, "FileVersion"),
            $"version projection: {project} file version");
        Equal(expected, Property(evaluation, "InformationalVersion"),
            $"version projection: {project} informational version");
    }

    private static string Property(JsonElement evaluation, string name)
        => evaluation.GetProperty("Properties").GetProperty(name).GetString()
           ?? throw new InvalidOperationException($"MSBuild property {name} is null");

    private static async Task<JsonElement> EvaluateAsync(string project)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = ParentDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
                 {
                     "msbuild", project, "-nologo",
                     "-getProperty:HistoryJanusVersion", "-getProperty:VersionPrefix",
                     "-getProperty:Version", "-getProperty:AssemblyVersion",
                     "-getProperty:FileVersion", "-getProperty:InformationalVersion",
                 })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
                            ?? throw new InvalidOperationException("Unable to start dotnet msbuild");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"MSBuild evaluation failed for {project}: {stderr}\n{stdout}");

        using var json = JsonDocument.Parse(stdout);
        return json.RootElement.Clone();
    }

    private static void AssertPackageConsumers()
    {
        var moduleProject = XDocument.Load(Path.Combine(
            ParentDir, "b-Code-Studio", "Module", "HistoryJanus.Module.csproj"));
        True(moduleProject.Descendants("Import").Any(item =>
                ((string?)item.Attribute("Project"))?.EndsWith("JanusVersion.props", StringComparison.OrdinalIgnoreCase) == true),
            "module consumption: module imports the single Janus version source");
        var moduleReferences = moduleProject.Descendants("Reference")
            .Select(item => (string?)item.Attribute("Include"))
            .Where(item => item is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Extensibility 是宿主 3.7.0 引入的演进层：它与 Core 的区别是稳定性承诺而非功能领域，
        // Core 冻结、Extensibility 随模块需要演进。Janus 目前不直接使用其中的类型，但装载时
        // 需要它在场（Services 对其有传递依赖）。
        True(moduleReferences.SetEquals(
                ["HistoryVulcan.Core", "HistoryVulcan.Extensibility", "HistoryVulcan.Services"]),
            "module consumption: module references only public HistoryVulcan host contracts");
        Equal(0, moduleProject.Descendants("PackageReference").Count(),
            "module consumption: module does not restore legacy HistoryVulcan packages");

        True(!File.Exists(Path.Combine(ParentDir, "b-Code-Studio", "Studio.csproj")),
            "single entry: legacy Studio project is removed");
        True(!File.Exists(Path.Combine(ParentDir, "b-Code-Studio", "Program.cs")),
            "single entry: legacy EXE entry is removed");
        foreach (var relative in new[]
                 {
                     "App.xaml", "App.xaml.cs", "app.manifest", "StartupMigrations.cs",
                     "Views/ConnectionSettingsView.xaml", "Views/ConnectionSettingsView.xaml.cs",
                 })
        {
            True(!File.Exists(Path.Combine(
                    ParentDir, "b-Code-Studio", relative.Replace('/', Path.DirectorySeparatorChar))),
                $"single entry: legacy host file is removed: {relative}");
        }
        True(!Directory.Exists(Path.Combine(ParentDir, "b-Code-Studio", "Connection"))
             && !Directory.Exists(Path.Combine(ParentDir, "b-Code-Studio", "Service")),
            "single entry: legacy connection and service trees are removed");

        var solution = File.ReadAllText(Path.Combine(ParentDir, "HistoryJanus.sln"));
        True(!solution.Contains("Studio.csproj", StringComparison.OrdinalIgnoreCase),
            "solution boundary: legacy Studio project is absent");
        Equal(4, File.ReadAllLines(Path.Combine(ParentDir, "HistoryJanus.sln")).Count(line =>
                line.StartsWith("Project(", StringComparison.Ordinal)
                && line.Contains(".csproj\"", StringComparison.OrdinalIgnoreCase)),
            "solution boundary: module and three verification projects are the only build projects");

        True(!Directory.Exists(Path.Combine(ParentDir, "b-Code-HistoryVulcan")),
            "repository boundary: HistoryVulcan source is not embedded in Janus");
        True(!Directory.Exists(Path.Combine(ParentDir, "z-HistoryVulcan")),
            "repository boundary: HistoryVulcan package repository is not duplicated in Janus");

        var build = File.ReadAllText(Path.Combine(
            ParentDir, "b-Code-Studio", "eng", "Build-HistoryJanusPackage.ps1"));
        True(!File.Exists(Path.Combine(ParentDir, "b-Code-Studio", "eng", "Publish-Janus.ps1"))
             && !File.Exists(Path.Combine(ParentDir, "b-Code-Studio", "eng", "Publish-Transaction.ps1")),
            "publish boundary: Janus has no project-owned formal publisher");
        Contains(build, "OutputRoot", "candidate build accepts Diana output root");
        Contains(build, "HistoryJanus.dll", "candidate build packages the module artifact");
        Contains(build, "Assert-ModulePackage",
            "candidate build uses an exact file-set and checksum gate");
        True(!build.Contains("DeployToZ", StringComparison.OrdinalIgnoreCase)
             && !Regex.IsMatch(build, @"(?m)^\s*\[switch\]\$Publish\b|\bif\s*\(\s*\$Publish\b",
                 RegexOptions.CultureInvariant),
            "candidate build cannot promote a formal snapshot");
        // 宿主契约版本的唯一真源是 JanusVersion.props；这里断言脚本从那里读取，
        // 而不是断言某个具体版本字面量——否则每次宿主升级都要同时改脚本和用例。
        Contains(build, "MinimumHistoryVulcanVersion",
            "candidate build host contract version comes from JanusVersion.props");

        True(!File.Exists(Path.Combine(ParentDir, "b-Code-Studio", "eng", "Test-Deploy-Janus.ps1")),
            "legacy development deployment entry is removed");
        True(!File.Exists(Path.Combine(ParentDir, "b-Code-Studio", "eng", "Deploy-Janus.ps1")),
            "legacy AppData dual-slot deployment entry is removed");
    }

    private static void AssertCurrentSourceAndDocumentation()
    {
        var studioRoot = Path.Combine(ParentDir, "b-Code-Studio");
        var historicalToken = new Regex(
            @"\bV\d+\.\d+(?:\.\d+)?\b|\bV\d+-[A-Z0-9]+\b|\bM\d+(?:\.\d+)?\b|\b(?!SHA-)[A-Z]{1,5}-\d{2,}(?:-\d+)?\b|\bQ\d{3,}(?:-\d+)?\b|\b[AD]\d{3,}(?:-\d+)?\b|移植自|原样迁入|今后每个版本|历史清理",
            RegexOptions.CultureInvariant);
        var productionFiles = Directory.EnumerateFiles(studioRoot, "*", SearchOption.AllDirectories)
            .Where(path => new[] { ".cs", ".xaml", ".props", ".csproj" }
                .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}",
                               StringComparison.OrdinalIgnoreCase)
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                               StringComparison.OrdinalIgnoreCase)
                           && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                               StringComparison.OrdinalIgnoreCase));
        foreach (var path in productionFiles)
        {
            True(!historicalToken.IsMatch(File.ReadAllText(path)),
                $"current source contains no release-history narration: {Path.GetRelativePath(ParentDir, path)}");
        }

        var officeRoot = Path.Combine(ParentDir, "b-Office");
        var currentRoot = Path.Combine(officeRoot, "current");
        var packageRoot = Path.Combine(officeRoot, "package");
        var historyRoot = Path.Combine(officeRoot, "history");
        True(Directory.Exists(currentRoot) && Directory.Exists(packageRoot) && Directory.Exists(historyRoot),
            "documentation follows the current/package/history contract");
        True(!Directory.Exists(Path.Combine(officeRoot, "HistoryJanus")),
            "documentation: the drifted HistoryJanus document directory is removed");
        True(!Directory.Exists(Path.Combine(officeRoot, "meta"))
             && !Directory.Exists(Path.Combine(officeRoot, "versions"))
             && !Directory.Exists(Path.Combine(officeRoot, "evidence")),
            "retired documentation directories are absent");
        var currentDocuments = Directory.EnumerateFiles(currentRoot, "*.md")
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.Ordinal);
        var packageDocuments = Directory.EnumerateFiles(packageRoot, "*.md")
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.Ordinal);
        True(currentDocuments.SetEquals(
                ["项目概览.md", "技术合同.md", "有效决策.md", "验证合同.md", "指令优化规范.md",
                    "AI开发工作流与分支图谱计划.md"]),
            "current contains the AIReady meta documents plus the command naming guide");
        True(packageDocuments.SetEquals(["模块API.md"]),
            "package contains only the Janus module API contract");
        var documentationCenter = Path.Combine(officeRoot, "文档中心.md");
        True(File.Exists(documentationCenter), "b-Office has a root documentation center");
        Equal(0, Directory.EnumerateFiles(officeRoot, "README.md", SearchOption.AllDirectories).Count(),
            "b-Office subdirectories contain no independent README");

        foreach (var path in Directory.EnumerateFiles(currentRoot, "*.md")
                     .Concat(Directory.EnumerateFiles(packageRoot, "*.md")))
        {
            True(!File.ReadAllLines(path).Any(line =>
                    line.StartsWith("> 适用版本：", StringComparison.Ordinal)
                    || line.StartsWith("> 当前版本：", StringComparison.Ordinal)),
                $"current manual has no hand-synchronized applicability version: {Path.GetFileName(path)}");
        }
        var officeNavigation = File.ReadAllText(documentationCenter);
        True(!officeNavigation.Contains("> 当前版本：", StringComparison.Ordinal),
            "documentation center has no hand-synchronized current version");

        var moduleManual = File.ReadAllText(Path.Combine(packageRoot, "模块API.md"));
        foreach (var forbidden in new[]
                 {
                     "计划为 4.0",
                     "下一代合同",
                     "下一条公共契约线",
                     "多来源/外部只读模块目录装载合同",
                 })
        {
            True(!moduleManual.Contains(forbidden, StringComparison.Ordinal),
                $"module manual does not predeclare HistoryVulcan roadmap: {forbidden}");
        }
    }

    private static async Task AssertPublishAreaGovernanceAsync()
    {
        var gitIgnore = File.ReadAllLines(Path.Combine(ParentDir, ".gitignore"));
        True(!gitIgnore.Any(line => line.Trim().Equals("stage/", StringComparison.Ordinal)),
            "version projection: the retired stage directory is not part of current governance");
        True(gitIgnore.Any(line => line.Trim().Equals("b-Publish/", StringComparison.Ordinal)),
            "version projection: local b-Publish build and history data is ignored");

        var gitAttributes = File.ReadAllLines(Path.Combine(ParentDir, ".gitattributes"));
        True(gitAttributes.Any(line => line.StartsWith("z-HistoryJanus/**/*.dll ", StringComparison.Ordinal)),
            "version projection: formal package binaries use Git LFS");
        True(!gitAttributes.Any(line => line.StartsWith("b-Publish/**/*.dll ", StringComparison.Ordinal)),
            "version projection: ignored local publish area has no tracked LFS contract");
        True(!Directory.Exists(Path.Combine(ParentDir, "z-Package")),
            "version projection: unnamed legacy package root is removed");
        var formalRoot = Path.Combine(ParentDir, "z-HistoryJanus");
        if (Directory.Exists(formalRoot))
        {
            var formalPackageFiles = Directory.EnumerateFiles(formalRoot, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(formalRoot, path).Replace('\\', '/'))
                .ToHashSet(StringComparer.Ordinal);
            var required = new[]
            {
                "HistoryJanus.dll", "HistoryJanus.xml", "module.manifest.json", "SHA256SUMS",
            };
            True(required.All(formalPackageFiles.Contains),
                "version projection: formal package has the runtime snapshot files");
            True(formalPackageFiles.All(path =>
                    required.Contains(path, StringComparer.Ordinal)
                    || (path.StartsWith("docs/", StringComparison.Ordinal)
                        && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))),
                "version projection: formal package is runtime files plus optional Markdown docs");
        }

        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = ParentDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("ls-files");
        start.ArgumentList.Add("b-Publish");
        using var process = Process.Start(start)
                            ?? throw new InvalidOperationException("Unable to inspect tracked b-Publish files");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        True(process.ExitCode == 0,
            $"version projection: git ls-files b-Publish succeeds: {stderr}");
        True(string.IsNullOrWhiteSpace(stdout),
            "version projection: b-Publish contains no tracked files");
    }

}
