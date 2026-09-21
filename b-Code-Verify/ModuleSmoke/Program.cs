using System.IO;
using System.Text.Json;
using HistoryVulcan.Core;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Modules;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.Services.Modules;

// 5.7.0：同时接受运行区根目录、单包和管线传入的 bin 输出。
// bin 经 SmokePackageRoot 包装为带校验和的隔离运行区，仍走正式发现器与装载器。
if (args.Length != 1)
{
    Console.Error.WriteLine("usage: ModuleSmoke <runtime-package-root|package|build-output>");
    return 2;
}

var packageRoot = Path.GetFullPath(args[0]);
if (!Directory.Exists(packageRoot))
{
    Console.Error.WriteLine($"runtime package root not found: {packageRoot}");
    return 2;
}

AppIdentity.Use(typeof(Program).Assembly);
var log = new MemoryLog();
var registry = new CommandRegistry();
var bus = new CommandBus(registry, log);

using var smokePackages = new SmokePackageRoot(packageRoot);
using var host = new ModuleHost(new RuntimeModuleDiscoverySource(smokePackages.Root), log)
{
    EnableUiModules = true,
    EnableFileWatching = false,
};

// 宿主 5.4：Attach 只接注册表与总线；设置与数据目录由模块自持，宿主不再注入。
host.Attach(registry, bus);
host.Start();

if (host.Modules.Count != 1)
{
    foreach (var entry in log.Snapshot())
        Console.Error.WriteLine($"[{entry.Level}] [{entry.Category}] {entry.Message}");
    foreach (var diagnostic in host.DiscoveryDiagnostics)
        Console.Error.WriteLine($"[discovery] {diagnostic.Code} {diagnostic.Path}: {diagnostic.Message}");
    throw new InvalidOperationException($"expected one module, got {host.Modules.Count}");
}

var manifestPath = host.Modules[0].ManifestPath
                   ?? throw new InvalidOperationException("discovered module carries no manifest path");
using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
var expectedVersion = manifest.RootElement.GetProperty("version").GetString();
// 5.6.0：39 条业务命令 + 8 条 UI 命令 + janus.status = 48（命令面与 5.5.1 相同）。
// 5.9.0：业务命令增至 42（proj.diff / proj.discard / gitrule.lfs），UI 仍 8 条 → 51。
// 5.11.0：业务命令增至 44（gitrule.lfsset / gitrule.lfsrepair），UI 增 ui.lfsdecide 到 9 条 → 54。
const int expectedRuntimeCommandCount = 54;

var meta = host.Modules[0];
if (!meta.ModuleName.Equals("HistoryJanus", StringComparison.Ordinal)
    || !meta.Version.Equals(expectedVersion, StringComparison.Ordinal)
    || meta.CommandCount != expectedRuntimeCommandCount)
{
    throw new InvalidOperationException(
        $"unexpected module metadata: {meta.ModuleName} {meta.Version} " +
        $"(manifest declares {expectedVersion}) commands={meta.CommandCount}");
}

foreach (var name in new[] { "janus.ui.describe", "janus.ui.actions", "janus.ui.data", "janus.ui.graphnode", "janus.ui.sectionenter" })
{
    if (!registry.TryGet(name, out var descriptor)
        || !descriptor.Readonly
        || !descriptor.HiddenReason!.Contains("界面", StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"descriptive UI command contract is invalid: {name}");
    }
}

foreach (var name in new[] { "janus.ui.refreshprojects", "janus.ui.projectaction", "janus.ui.openmeta" })
{
    if (!registry.TryGet(name, out var descriptor)
        || descriptor.Readonly
        || !descriptor.HiddenReason!.Contains("界面", StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"local UI command contract is invalid: {name}");
    }
}

string[] pageIds = [];
var describe = await bus.ExecuteAsync("janus.ui.describe", "ModuleSmoke");
if (!describe.Success)
    throw new InvalidOperationException(describe.Message);
using (var description = JsonDocument.Parse(describe.Message))
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

var actions = await bus.ExecuteAsync("janus.ui.actions", "ModuleSmoke");
if (!actions.Success)
    throw new InvalidOperationException($"invalid action declaration: {actions.Message}");
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
    if (!actions.Message.Contains(action, StringComparison.Ordinal))
        throw new InvalidOperationException($"action declaration is missing {action}: {actions.Message}");
}

var data = await bus.ExecuteAsync("janus.ui.data view=projects", "ModuleSmoke");
if (!data.Success)
    throw new InvalidOperationException($"page data command failed: {data.Message}");

using (var projectDocument = JsonDocument.Parse(data.Message))
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

var commandCount = registry.All().Count;
host.Reload();
if (registry.All().Count != commandCount
    || !registry.TryGet("janus.ui.describe", out _)
    || !registry.TryGet("janus.proj.list", out _))
{
    throw new InvalidOperationException("module reload did not replace the command snapshot cleanly");
}

// 卸载走 ModuleHost.Unload：5.1.x 拿掉了 ChangeDirectory，运行区根目录在构造时定死，
// 「换一个空目录再看命令还在不在」这条老写法没有对应面了，而它要证的事没变——
// 模块下去之后登记表里不许留 janus.*。
host.Unload("HistoryJanus");
if (registry.All().Any(command => command.Name.StartsWith("janus.", StringComparison.Ordinal)))
    throw new InvalidOperationException("module unload left Janus commands in the host registry");

Console.WriteLine(
    $"PASS module={meta.ModuleName} version={meta.Version} commands={commandCount} " +
    $"pages={string.Join(",", pageIds)} protocol=V1");
return 0;

sealed class MemorySettings : ISettingsService
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public string? Get(string key) => _values.GetValueOrDefault(key);
    public int GetInt(string key, int fallback) => int.TryParse(Get(key), out var value) ? value : fallback;
    public void Set(string key, string value) => _values[key] = value;
    public IReadOnlyList<KeyValuePair<string, string>> All() => _values.ToList();
}

sealed class MemoryLog : IShellLog
{
    private readonly List<ShellLogEntry> _entries = [];

    public event EventHandler<ShellLogEntry>? EntryAdded;
    public IReadOnlyList<ShellLogEntry> Snapshot() => _entries;

    public void Log(ShellLogLevel level, string category, string message)
    {
        var entry = new ShellLogEntry(DateTime.Now, level, category, message);
        _entries.Add(entry);
        EntryAdded?.Invoke(this, entry);
    }
}
