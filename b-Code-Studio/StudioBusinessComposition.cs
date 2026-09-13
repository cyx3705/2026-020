using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryJanus.Git;
using HistoryJanus.GitHub;

namespace HistoryJanus;

/// <summary>
/// Janus business services registered into a host-owned command bus.
/// This boundary deliberately owns no process, window, module loader, MCP, Web, or LAN lifetime.
/// </summary>
public sealed class StudioBusinessComposition
{
    internal StudioBusinessComposition(
        ProjectService projects,
        HistoryRecorder history,
        GitFileRuleService gitRules,
        BranchHistoryService branchHistory,
        GitHubConnectionService gitHub,
        GraphService graph)
    {
        Projects = projects;
        History = history;
        GitRules = gitRules;
        BranchHistory = branchHistory;
        GitHub = gitHub;
        Graph = graph;
    }

    public ProjectService Projects { get; }

    public HistoryRecorder History { get; }

    public GitFileRuleService GitRules { get; }

    public BranchHistoryService BranchHistory { get; }

    public GitHubConnectionService GitHub { get; }

    public GraphService Graph { get; }
}

/// <summary>
/// Builds the Janus domain graph inside infrastructure supplied by the AppShell host.
/// </summary>
public static class StudioBusinessCompositionFactory
{
    public static StudioBusinessComposition Register(
        CommandRegistry registry,
        CommandBus bus,
        ISettingsService settings,
        IShellLog log,
        string dataDirectory,
        string commandSource = "app",
        Func<string?>? currentProjectName = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandSource);

        var history = new HistoryRecorder(dataDirectory, log);
        var projects = new ProjectService(
            settings,
            bus.RequestConfirmation,
            dataDirectory);
        projects.EnsureDefaultSettings();
        projects.NotesProvider = history.AllNotes;
        // 首次推送时按需建远端仓库；令牌来自 janus.github.login 已存进 GCM 的 HTTPS 凭据。
        projects.RepositoryProvisioner = new GitHubRepositoryProvisioner();

        var gitRules = new GitFileRuleService(projects, settings);
        // 排除规则由提交链路自动落地，不再需要人工下发命令。
        projects.ExcludeRules = gitRules;
        var branchHistory = new BranchHistoryService(projects);
        // GitHub 事实读取指向当前选中项目仓；无选中时回退库根（非 git 仓则诊断失败）
        var gitHub = new GitHubConnectionService(
            () => projects.ResolveGitHubRepository(currentProjectName?.Invoke()));
        var graph = new GraphService(projects);

        ProjectCommands.RegisterAll(registry, projects, history, commandSource);
        BranchHistoryCommands.RegisterAll(registry, branchHistory, history, commandSource);
        GitRuleCommands.RegisterAll(registry, gitRules, commandSource);
        GitHubCommands.RegisterAll(registry, gitHub, commandSource);
        GraphCommands.RegisterAll(registry, graph, commandSource);
        // 业务模块不注册诊断或自动化辅助指令：日志承压由宿主 vulcan.log.flood 承担，
        // 不在此重复实现。

        return new StudioBusinessComposition(
            projects,
            history,
            gitRules,
            branchHistory,
            gitHub,
            graph);
    }
}
