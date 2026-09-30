using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;

// 5.13.0：装载冒烟改走宿主命令行 `HistoryVulcan.Cli.exe --probe`（宿主 5.9.0 统一契约，DEC-070）。
// 此前直接 new 宿主的 ModuleHost，宿主实现程序集 Services 因此成了本仓的编译期依赖；宿主两次改内部都把这里打得编不过。
// 现在只看宿主给出的装载结果与指令结果（JSON 形状），宿主内部怎么改都不影响本仓。
// 重载、卸载后注册表是否干净是宿主的行为，由宿主自己的测试守住，这里不再重复。
//
// 5.7.0：同时接受运行区根目录、单包和管线传入的 bin 输出；bin 经 SmokePackageRoot 包装为带校验和的包。
if (args.Length != 1)
{
    Console.Error.WriteLine("usage: ModuleSmoke <runtime-package-root|package|build-output>");
    return 2;
}

var input = Path.GetFullPath(args[0]);
if (!Directory.Exists(input))
{
    Console.Error.WriteLine($"runtime package root not found: {input}");
    return 2;
}

using var smokePackages = new SmokePackageRoot(input);
var package = smokePackages.Package;
using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(package, "module.manifest.json")));
var expectedVersion = manifest.RootElement.GetProperty("version").GetString();

// 5.6.0：39 条业务命令 + 8 条 UI 命令 + janus.status = 48（命令面与 5.5.1 相同）。
// 5.9.0：业务命令增至 42（proj.diff / proj.discard / gitrule.lfs），UI 仍 8 条 → 51。
// 5.11.0：业务命令增至 44（gitrule.lfsset / gitrule.lfsrepair），UI 增 ui.lfsdecide 到 9 条 → 54。
const int expectedRuntimeCommandCount = 54;

var listed = Probe(package, "vulcan.command.list domain=janus");
var module = listed.GetProperty("data").GetProperty("module");
if (!module.GetProperty("attached").GetBoolean())
{
    foreach (var line in listed.GetProperty("diagnostics").EnumerateArray())
        Console.Error.WriteLine($"[discovery] {line.GetString()}");
    throw new InvalidOperationException(
        "module did not attach: " + string.Join("; ", module.GetProperty("attachFailures").EnumerateArray().Select(item => item.GetString())));
}

if (module.GetProperty("name").GetString() != "HistoryJanus"
    || module.GetProperty("version").GetString() != expectedVersion
    || module.GetProperty("commandCount").GetInt32() != expectedRuntimeCommandCount)
{
    throw new InvalidOperationException(
        $"unexpected module metadata: {module.GetProperty("name")} {module.GetProperty("version")} " +
        $"(manifest declares {expectedVersion}) commands={module.GetProperty("commandCount")}");
}

// 指令属性从目录行读（JSON 字段名），不再查宿主注册表。
var rows = listed.GetProperty("data").GetProperty("result").GetProperty("data").EnumerateArray()
    .ToDictionary(row => row.GetProperty("commandName").GetString()!, row => row, StringComparer.Ordinal);
foreach (var name in new[] { "janus.ui.describe", "janus.ui.actions", "janus.ui.data", "janus.ui.graphnode", "janus.ui.sectionenter" })
{
    if (!rows.TryGetValue(name, out var row)
        || !row.GetProperty("readonly").GetBoolean()
        || row.GetProperty("hiddenReason").GetString()?.Contains("界面", StringComparison.Ordinal) != true)
    {
        throw new InvalidOperationException($"descriptive UI command contract is invalid: {name}");
    }
}

foreach (var name in new[] { "janus.ui.refreshprojects", "janus.ui.projectaction", "janus.ui.openmeta" })
{
    if (!rows.TryGetValue(name, out var row)
        || row.GetProperty("readonly").GetBoolean()
        || row.GetProperty("hiddenReason").GetString()?.Contains("界面", StringComparison.Ordinal) != true)
    {
        throw new InvalidOperationException($"local UI command contract is invalid: {name}");
    }
}

string[] pageIds;
var describe = Result(Probe(package, "janus.ui.describe"));
using (var description = JsonDocument.Parse(describe))
{
    var root = description.RootElement;
    if (root.GetProperty("schemaVersion").GetInt32() != 1
        || root.GetProperty("owner").GetString() != "HistoryJanus")
        throw new InvalidOperationException("invalid page description identity");
    var ids = root.GetProperty("pages").EnumerateArray()
        .Select(page => page.GetProperty("id").GetString())
        .ToArray();
    // 5.4.6：rules / history / github 收进 projops 的 switch 容器，不再是页面（REQ-015）。
    if (!new[] { "overview", "graph", "projops" }
            .SequenceEqual(ids, StringComparer.Ordinal))
        throw new InvalidOperationException($"unexpected page ids: {string.Join(",", ids)}");
    pageIds = ids!;
}

var actions = Result(Probe(package, "janus.ui.actions"));
foreach (var action in new[]
         {
             "janus.project.rename",
             "janus.project.create",
             "janus.project.commit",
             "janus.project.push",
             "janus.projects.refresh",
             "janus.project.openmeta",
             "janus.project.action",
             "janus.graph.node.detail",
         })
{
    if (!actions.Contains(action, StringComparison.Ordinal))
        throw new InvalidOperationException($"action declaration is missing {action}: {actions}");
}

var data = Result(Probe(package, "janus.ui.data view=projects"));
using (var projectDocument = JsonDocument.Parse(data))
{
    var firstProject = projectDocument.RootElement.EnumerateArray().FirstOrDefault();
    if (firstProject.ValueKind != JsonValueKind.Object
        || !firstProject.TryGetProperty("name", out _)
        || !firstProject.TryGetProperty("isClean", out _)
        || !firstProject.TryGetProperty("subject", out _)
        || firstProject.TryGetProperty("BranchName", out _))
    {
        throw new InvalidOperationException("project page data does not use the descriptive row shape");
    }
}

Console.WriteLine(
    $"PASS module={module.GetProperty("name")} version={module.GetProperty("version")} " +
    $"commands={module.GetProperty("commandCount")} pages={string.Join(",", pageIds)} protocol=V1 host=--probe");
return 0;

// 成功指令的正文；失败直接抛出，信息里带宿主给的原因。
static string Result(JsonElement envelope)
{
    var result = envelope.GetProperty("data").GetProperty("result");
    var message = result.GetProperty("message").GetString() ?? "";
    if (!result.GetProperty("success").GetBoolean())
        throw new InvalidOperationException(message);
    return message;
}

// 调已发布宿主的 `HistoryVulcan.Cli.exe --probe <包> --cli <指令> --format json`。
// 宿主根目录在构建时写进本程序集（csproj 的 HistoryVulcanHostRoot），管线在工作区里会传 HistoryVulcanPackageRoot。
static JsonElement Probe(string package, string command)
{
    var hostRoot = Assembly.GetExecutingAssembly()
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(item => item.Key == "HistoryVulcanHostRoot")?.Value;
    if (string.IsNullOrEmpty(hostRoot))
        throw new InvalidOperationException("构建时没有写入 HistoryVulcanHostRoot。");
    var cli = Path.Combine(hostRoot, "HistoryVulcan.Cli.exe");
    if (!File.Exists(cli))
        throw new InvalidOperationException($"找不到宿主命令行：{cli}（宿主 5.9.0 起提供 --probe）");

    var start = new ProcessStartInfo(cli)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        StandardOutputEncoding = Encoding.UTF8,
    };
    foreach (var argument in new[] { "--probe", package, "--format", "json", "--cli" }.Concat(command.Split(' ')))
        start.ArgumentList.Add(argument);

    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (!output.TrimStart().StartsWith('{'))
        throw new InvalidOperationException($"--probe 没有输出 JSON（退出码 {process.ExitCode}）：{output}{error}");
    using var document = JsonDocument.Parse(output);
    return document.RootElement.Clone();
}
