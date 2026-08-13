using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HistoryVulcan.Core;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Docking;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Modules;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.Services.Modules;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: ModuleSmoke <module-directory>");
    return 2;
}

var moduleDirectory = Path.GetFullPath(args[0]);
if (!Directory.Exists(moduleDirectory))
{
    Console.Error.WriteLine($"module directory not found: {moduleDirectory}");
    return 2;
}

AppIdentity.Use(typeof(Program).Assembly);
var log = new MemoryLog();
var registry = new CommandRegistry();
var bus = new CommandBus(registry, log);
var settings = new MemorySettings();
var dataDirectory = Path.Combine(Path.GetTempPath(), "HistoryJanus-ModuleSmoke", Guid.NewGuid().ToString("N"));
var shellUi = new RecordingShellUiRegistrar();
registry.Register(new CommandDescriptor
{
    Name = "janus.status",
    Summary = "frontend proxy placeholder",
    Readonly = true,
    Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("proxy")),
}, "frontend:HistoryVulcan.Frontend");
using var host = new ModuleHost(moduleDirectory, log)
{
    EnableCommands = true,
    EnableUiModules = true,
    EnableFileWatching = false,
    // ModuleHost commits its snapshot through the host UI synchronization context.
    // The real HistoryVulcan supplies WPF's DispatcherSynchronizationContext; this
    // synchronous context keeps the smoke deterministic without creating WPF UI.
    UiContext = new ImmediateSynchronizationContext(),
    ShellUi = shellUi,
};

host.Attach(registry, bus, settings, dataDirectory);
host.Start();

if (host.Modules.Count != 1)
{
    foreach (var entry in log.Snapshot())
        Console.Error.WriteLine($"[{entry.Level}] [{entry.Category}] {entry.Message}");
    throw new InvalidOperationException($"expected one module, got {host.Modules.Count}");
}

// 期望版本取自被测目录的 module.manifest.json，而不是写死字面量：
// 装载出的模块身份必须与包自己声明的版本一致，且升版本时无需再改这个用例。
var manifestPath = Path.Combine(moduleDirectory, "module.manifest.json");
if (!File.Exists(manifestPath))
    throw new InvalidOperationException($"module manifest not found: {manifestPath}");

using var manifestJson = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath));
var expectedVersion = manifestJson.RootElement.GetProperty("version").GetString();
const int expectedRuntimeCommandCount = 40;

var meta = host.Modules[0];
if (!meta.ModuleName.Equals("HistoryJanus", StringComparison.Ordinal)
    || !meta.Version.Equals(expectedVersion, StringComparison.Ordinal)
    || !meta.Ui
    || meta.CommandCount != expectedRuntimeCommandCount)
{
    throw new InvalidOperationException(
        $"unexpected module metadata: {meta.ModuleName} {meta.Version} " +
        $"(manifest declares {expectedVersion}) ui={meta.Ui} commands={meta.CommandCount}");
}

if (!registry.TryGet("janus.status", out var descriptor)
    || !descriptor.Readonly
    || !registry.GetSource("janus.status")
        .Equals("module:HistoryJanus", StringComparison.Ordinal))
{
    throw new InvalidOperationException(
        $"module command contract is not projected correctly: exists={registry.TryGet("janus.status", out _)} "
        + $"source={registry.GetSource("janus.status")} "
        + string.Join("; ", log.Snapshot().Where(entry => entry.Category == "module").Select(entry => entry.Message)));
}

var businessCommands = new[]
{
    "janus.proj.list",
    "janus.proj.tree",
    "janus.proj.metas",
    "janus.proj.metaopen",
    "janus.proj.commit",
    "janus.proj.push",
    "janus.history.list",
    "janus.history.rollback",
    "janus.history.reset",
    "janus.history.forcepush",
    "janus.gitrule.list",
    "janus.gitrule.batchset",
    "janus.github.status",
    "janus.github.accounts",
    "janus.github.test",
    "janus.github.login",
    "janus.github.logout",
    "janus.github.identity",
    "janus.github.remote",
    "janus.graph.summary",
    "janus.graph.commits",
};
foreach (var commandName in businessCommands)
{
    if (!registry.TryGet(commandName, out _)
        || !registry.GetSource(commandName).Equals("module:HistoryJanus", StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"business command is not module-owned: {commandName}");
    }
}
var result = await bus.ExecuteAsync("janus.status", "ModuleSmoke");
if (!result.Success || !result.Message.Contains(expectedVersion, StringComparison.Ordinal))
    throw new InvalidOperationException($"module command failed: {result.Message}");

var projectList = await bus.ExecuteAsync("janus.proj.list", "ModuleSmoke");
if (!projectList.Success)
    throw new InvalidOperationException($"real project command failed: {projectList.Message}");

// github 页已并入项目操作页底部分段；图谱独占 graph 窗口。
var expectedWindows = new[] { "overview", "graph", "projops" };
var actualWindows = shellUi.Descriptors.Select(item => item.Id).ToArray();
if (!expectedWindows.SequenceEqual(actualWindows, StringComparer.Ordinal))
{
    throw new InvalidOperationException(
        $"unexpected module windows: [{string.Join(", ", actualWindows)}]");
}

var windowsById = shellUi.Descriptors.ToDictionary(item => item.Id, StringComparer.Ordinal);
AssertOverviewCenterTool(windowsById["overview"]);
AssertGraphJoinsConsole(windowsById["graph"]);
AssertPlacement(windowsById["projops"], DockSide.Left, 0.38);

if (shellUi.Descriptors.Any(item => item.Title.Equals("HistoryJanus", StringComparison.Ordinal)))
    throw new InvalidOperationException("placeholder main window is still registered");

var pageTypes = ConstructPages(shellUi.Descriptors, bus);
// GitHubConnectionView 不再是宿主页面，它由 ProjectOperationsView 内嵌为第三个分段。
var expectedPageTypes = new[]
{
    "OverviewView",
    "GraphView",
    "ProjectOperationsView",
};
if (!expectedPageTypes.SequenceEqual(pageTypes, StringComparer.Ordinal))
    throw new InvalidOperationException($"unexpected page types: [{string.Join(", ", pageTypes)}]");

var commandCount = registry.All().Count;
if (commandCount != expectedRuntimeCommandCount)
    throw new InvalidOperationException($"expected {expectedRuntimeCommandCount} runtime commands, got {commandCount}");

var moduleSource = registry.GetSource(descriptor.Name);
host.Reload();
if (registry.All().Count != commandCount
    || businessCommands.Any(commandName => !registry.TryGet(commandName, out _)))
{
    throw new InvalidOperationException("module reload did not replace the business command snapshot cleanly");
}

var emptyModuleDirectory = Path.Combine(dataDirectory, "empty-modules");
Directory.CreateDirectory(emptyModuleDirectory);
host.ChangeDirectory(emptyModuleDirectory);
if (businessCommands.Any(commandName => registry.TryGet(commandName, out _))
    || registry.TryGet("janus.status", out _))
{
    throw new InvalidOperationException("module unload left owned commands in the host registry");
}

var serviceRegistry = new CommandRegistry();
var serviceBus = new CommandBus(serviceRegistry, log);
var serviceReloads = 0;
using (var serviceHost = new ModuleHost(moduleDirectory, log)
{
    EnableCommands = true,
    EnableUiModules = false,
    EnableFileWatching = false,
})
{
    serviceHost.ReloadCompleted += () => serviceReloads++;
    serviceHost.Attach(serviceRegistry, serviceBus, settings, dataDirectory);
    serviceHost.Start();
    if (serviceReloads != 1
        || serviceRegistry.All().Count != expectedRuntimeCommandCount
        || !serviceRegistry.TryGet("janus.proj.list", out _)
        || !serviceRegistry.TryGet("janus.gitrule.list", out _))
    {
        throw new InvalidOperationException(
            "headless service host did not publish the module business commands");
    }
}
if (serviceRegistry.TryGet("janus.proj.list", out _))
    throw new InvalidOperationException("disposing the headless host left module commands registered");

Console.WriteLine(
    $"PASS module={meta.ModuleName} version={meta.Version} commands={meta.CommandCount} "
    + $"source={moduleSource} windows={string.Join(",", actualWindows)} "
    + $"pages={string.Join(",", pageTypes)}");

return 0;

static IReadOnlyList<string> ConstructPages(
    IReadOnlyList<ToolWindowDescriptor> descriptors,
    CommandBus expectedBus)
{
    List<string>? pageTypes = null;
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            pageTypes = descriptors.Select(descriptor =>
            {
                var page = descriptor.ContentFactory?.Invoke()
                           ?? throw new InvalidOperationException(
                               $"window {descriptor.Id} has no content factory");
                var pageType = page.GetType();
                var busField = pageType.GetField("_busAccessor",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (busField != null)
                {
                    var accessor = busField.GetValue(page) as Func<CommandBus?>
                                   ?? throw new InvalidOperationException(
                                       $"window {descriptor.Id} does not retain the host bus accessor");
                    if (!ReferenceEquals(accessor(), expectedBus))
                        throw new InvalidOperationException(
                            $"window {descriptor.Id} is not connected to the host command bus");
                }
                else
                {
                    // github 页面直连业务服务(写操作仅限 UI),验证其服务访问器已接线;
                    // 本宿主不引用模块程序集,经 Delegate 反射调用以避免类型耦合
                    var serviceField = pageType.GetField("_serviceAccessor",
                        BindingFlags.Instance | BindingFlags.NonPublic)
                        ?? throw new InvalidOperationException(
                            $"window {descriptor.Id} retains no host accessor");
                    if (serviceField.GetValue(page) is not Delegate serviceAccessor
                        || serviceAccessor.DynamicInvoke() == null)
                        throw new InvalidOperationException(
                            $"window {descriptor.Id} is not connected to the github service");
                }
                if (page is Control control)
                {
                    control.Resources["Shell.Brush.TextPrimary"] = Brushes.Black;
                    var lightForeground = control.Foreground;
                    control.Resources["Shell.Brush.TextPrimary"] = Brushes.White;
                    var darkForeground = control.Foreground;
                    if (lightForeground != Brushes.Black || darkForeground != Brushes.White)
                    {
                        throw new InvalidOperationException(
                            $"window {descriptor.Id} did not resolve dynamic text theme resources");
                    }
                    VerifyOperationSegmentTheme(control);
                }
                return page.GetType().Name;
            }).ToList();
        }
        catch (Exception ex)
        {
            failure = ex;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    if (!thread.Join(TimeSpan.FromSeconds(15)))
        throw new TimeoutException("page construction did not complete within 15 seconds");
    if (failure != null)
        throw new InvalidOperationException("page construction failed", failure);
    return pageTypes ?? throw new InvalidOperationException("page construction produced no result");
}

static void AssertOverviewCenterTool(ToolWindowDescriptor descriptor)
{
    // 总览占中央工作区，且不得再依赖别的模块的标签组：DefaultTabTarget 必须为空，
    // 否则落位又会取决于模块装载顺序。
    if (descriptor.DefaultSide != DockSide.Center
        || !string.IsNullOrEmpty(descriptor.DefaultTabTarget))
    {
        throw new InvalidOperationException(
            $"overview must dock to the center with no tab target, got {descriptor.DefaultSide} target={descriptor.DefaultTabTarget}");
    }
}

static void AssertGraphJoinsConsole(ToolWindowDescriptor descriptor)
{
    if (descriptor.DefaultSide != DockSide.Tab
        || !string.Equals(descriptor.DefaultTabTarget, StandardWindowIds.Console, StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            $"graph must join the host console tab group, got {descriptor.DefaultSide} target={descriptor.DefaultTabTarget}");
    }
}

static void AssertPlacement(ToolWindowDescriptor descriptor, DockSide side, double ratio)
{
    if (descriptor.DefaultSide != side || Math.Abs(descriptor.DefaultRatio - ratio) > 0.0001)
    {
        throw new InvalidOperationException(
            $"unexpected placement for {descriptor.Id}: {descriptor.DefaultSide} {descriptor.DefaultRatio}");
    }
}

static void VerifyOperationSegmentTheme(Control page)
{
    if (page.GetType().Name != "ProjectOperationsView" || page is not FrameworkElement scope)
        return;

    var names = new[]
    {
        "CurrentSubmodulesModeButton", "CurrentBothModeButton",
        "AllSubmodulesModeButton", "AllBothModeButton",
    };
    var buttons = names.Select(name => scope.FindName(name) as RadioButton
        ?? throw new InvalidOperationException($"operation segment is missing: {name}")).ToArray();

    SetTheme(page.Resources, Brushes.Black, Brushes.White, Brushes.LightYellow,
        Brushes.LightGray, Brushes.Gray);
    AssertSegmentTheme(buttons, Brushes.Black, Brushes.White, Brushes.LightYellow);

    SetTheme(page.Resources, Brushes.White, Brushes.Black, Brushes.DarkOliveGreen,
        Brushes.DimGray, Brushes.Gray);
    AssertSegmentTheme(buttons, Brushes.White, Brushes.Black, Brushes.DarkOliveGreen);

    var disabled = buttons[0];
    disabled.IsEnabled = false;
    if (disabled.Foreground != Brushes.Gray)
        throw new InvalidOperationException("disabled operation segment did not use TextDisabled");
}

static void SetTheme(
    ResourceDictionary resources,
    Brush text,
    Brush surface,
    Brush accentSoft,
    Brush surfaceHover,
    Brush disabled)
{
    resources["Shell.Brush.TextPrimary"] = text;
    resources["Shell.Brush.TextDisabled"] = disabled;
    resources["Shell.Brush.SurfaceAlt"] = surface;
    resources["Shell.Brush.SurfaceHover"] = surfaceHover;
    resources["Shell.Brush.AccentSoft"] = accentSoft;
    resources["Shell.Brush.Accent"] = Brushes.Goldenrod;
    resources["Shell.Brush.ControlBorder"] = Brushes.Gray;
}

static void AssertSegmentTheme(
    IEnumerable<RadioButton> buttons,
    Brush expectedText,
    Brush expectedSurface,
    Brush expectedSelectedSurface)
{
    foreach (var button in buttons)
    {
        button.IsEnabled = true;
        button.ApplyTemplate();
        var border = button.Template.FindName("SegmentBorder", button) as Border
                     ?? throw new InvalidOperationException("operation segment template border is missing");
        var expectedBackground = button.IsChecked == true
            ? expectedSelectedSurface
            : expectedSurface;
        if (button.Foreground != expectedText || border.Background != expectedBackground)
        {
            throw new InvalidOperationException(
                $"operation segment theme mismatch: {button.Name} checked={button.IsChecked}");
        }
    }
}

sealed class ImmediateSynchronizationContext : SynchronizationContext
{
    public override void Send(SendOrPostCallback callback, object? state) => callback(state);

    public override void Post(SendOrPostCallback callback, object? state) => callback(state);
}

sealed class RecordingShellUiRegistrar : IShellUiRegistrar
{
    public List<ToolWindowDescriptor> Descriptors { get; } = [];

    public bool IsUiThread => true;

    public void Invoke(Action action) => action();

    public IDisposable RegisterToolWindow(ToolWindowDescriptor descriptor, string owner)
    {
        Descriptors.Add(descriptor);
        return new Registration(() => Descriptors.Remove(descriptor));
    }

    public void UnregisterToolWindow(string id)
        => Descriptors.RemoveAll(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public void UnregisterOwner(string owner) => Descriptors.Clear();

    private sealed class Registration(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
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

sealed class MemorySettings : ISettingsService
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public string? Get(string key) => _values.GetValueOrDefault(key);

    public int GetInt(string key, int fallback)
        => int.TryParse(Get(key), out var value) ? value : fallback;

    public void Set(string key, string value) => _values[key] = value;

    public IReadOnlyList<KeyValuePair<string, string>> All() => _values.ToList();
}
