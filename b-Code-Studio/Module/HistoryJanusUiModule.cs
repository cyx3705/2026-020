using System.IO;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Docking;
using HistoryVulcan.Core.Modules;
using HistoryJanus.GitHub;
using HistoryJanus.Views;

namespace HistoryJanus.Module;

/// <summary>Registers the existing Janus business pages into the AppShell host.</summary>
public sealed class HistoryJanusUiModule : IUiModule, IShellUiAware, IModuleContextAware
{
    private readonly List<IDisposable> _registrations = [];
    private readonly ProjectSelectionState _selection = new();
    private IShellUiRegistrar? _shellUi;
    private IModuleContext? _context;
    private StudioBusinessComposition? _business;

    IShellUiRegistrar IShellUiAware.ShellUi
    {
        set => _shellUi = value;
    }

    public void Attach(IModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_context != null)
            throw new InvalidOperationException("HistoryJanus module context is already attached.");

        _context = context;
        context.RegisterCommands(registry =>
        {
            _business = StudioBusinessCompositionFactory.Register(
                registry,
                context.Bus,
                context.Settings,
                context.Log,
                Path.Combine(context.DataDirectory, "HistoryJanus"),
                "module:HistoryJanus",
                () => _selection.CurrentProjectName);
        });
    }

    public void CreateUi()
    {
        if (_shellUi == null || _registrations.Count != 0)
            return;

        Func<CommandBus?> busAccessor = () => _context?.Bus;
        Func<string, bool> isProtected = branch => _business?.Projects.IsProtected(branch) == true;
        Func<GitHubConnectionService?> gitHubAccessor = () => _business?.GitHub;
        foreach (var descriptor in CreateDescriptors(busAccessor, _selection, isProtected, gitHubAccessor))
            _registrations.Add(_shellUi.RegisterToolWindow(descriptor, "HistoryJanus"));
    }

    public void DestroyUi()
    {
        for (var index = _registrations.Count - 1; index >= 0; index--)
            _registrations[index].Dispose();
        _registrations.Clear();
    }

    public static IReadOnlyList<ToolWindowDescriptor> CreateDescriptors(
        Func<CommandBus?> busAccessor,
        ProjectSelectionState selection,
        Func<string, bool> isProtected,
        Func<GitHubConnectionService?> gitHubAccessor)
    {
        ArgumentNullException.ThrowIfNull(busAccessor);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(isProtected);
        ArgumentNullException.ThrowIfNull(gitHubAccessor);

        return
        [
            new ToolWindowDescriptor
            {
                Id = "overview",
                Title = "项目总览",
                // 主窗口（中央工作区）。此前挂在 Mcp 标签组上，但那个窗口由 Mercury 提供，
                // 模块装载顺序不保证它先到——先到就并入、没到就回退右侧停靠，
                // 首次启动的落位因此不稳定（日志里的「标签组目标 mcp 不可用」）。
                // 中央区不依赖任何别的模块，落位才是确定的。
                DefaultSide = DockSide.Center,
                IsSingleton = true,
                ContentFactory = () => new OverviewView(busAccessor, selection),
            },
            new ToolWindowDescriptor
            {
                Id = "graph",
                Title = "分支图谱",
                // 并入宿主控制台标签组：控制台由 HistoryVulcan 自己注册，始终先于模块存在，
                // 不像 DefaultTabTarget=mcp 会依赖 Mercury 装载顺序。
                DefaultSide = DockSide.Tab,
                DefaultTabTarget = StandardWindowIds.Console,
                IsSingleton = true,
                ContentFactory = () => new GraphView(busAccessor, selection),
            },
            new ToolWindowDescriptor
            {
                Id = "projops",
                Title = "项目操作",
                // 左侧：与中央区的总览左右分工，宽度要放得下分段行与规则表格。
                DefaultSide = DockSide.Left,
                DefaultRatio = 0.38,
                IsSingleton = true,
                // 底部同一行分段切换 Git 文件规则 / 分支历史 / GitHub，三者都内嵌于本页；
                // 没有独立 github 窗口。
                    ContentFactory = () => new ProjectOperationsView(
                    busAccessor,
                    selection,
                    isProtected,
                    gitHubAccessor),
            },
        ];
    }
}
