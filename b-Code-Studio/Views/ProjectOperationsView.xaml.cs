using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using HistoryVulcan.Core.Commands;
using HistoryJanus.Git;
using HistoryJanus.GitHub;

namespace HistoryJanus.Views;

/// <summary>项目创建、提交推送和所选项目的三状态 Git 文件格式规则。</summary>
public partial class ProjectOperationsView : UserControl
{
    private readonly Func<CommandBus?> _busAccessor;
    private readonly ProjectSelectionState _selection;
    private readonly Func<string, bool> _isProtected;
    private List<string> _projectNames = [];
    private bool _projectOperationRunning;
    private string? _nameBoxProject;

    public ProjectOperationsView(Func<CommandBus?> busAccessor, ProjectSelectionState selection,
        Func<string, bool> isProtected, Func<GitHubConnectionService?> gitHubAccessor)
    {
        InitializeComponent();
        _busAccessor = busAccessor;
        _selection = selection;
        _isProtected = isProtected;
        HistoryPanel.Content = new BranchHistoryView(busAccessor, selection, isProtected);
        // GitHub 连接治理是本页第三个分段，不是宿主级独立窗口。
        GitHubPanel.Content = new GitHubConnectionView(gitHubAccessor, busAccessor);
        SelectedCommitMessageBox.Text = "一键推送更新";
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ViewKit.RunOnceOnLoaded(this, () => RefreshProjectsAsync(_selection.CurrentProjectName));
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        _selection.Changed -= OnSharedSelectionChanged;
        _selection.Changed += OnSharedSelectionChanged;
        Dispatcher.BeginInvoke(async () => await ApplySharedSelectionAsync());
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
        => _selection.Changed -= OnSharedSelectionChanged;

    // 项目选择唯一真源是共享 ProjectSelectionState（项目总览页驱动）；本页只跟随
    private async Task RefreshProjectsAsync(string? select = null)
    {
        if (_busAccessor() is not { } bus)
            return;
        var result = await bus.ExecuteAsync("janus.proj.list", "UI");
        if (!result.Success || !ModuleResultData.TryRead(result.Data, out List<WorktreeInfo>? projects))
            return;
        _projectNames = projects.Select(project => project.BranchName).ToList();
        var requested = select ?? _selection.CurrentProjectName;
        var selected = _projectNames.FirstOrDefault(name =>
            name.Equals(requested, StringComparison.OrdinalIgnoreCase)) ?? _projectNames.FirstOrDefault();
        if (!string.Equals(selected, _selection.CurrentProjectName, StringComparison.OrdinalIgnoreCase))
        {
            _selection.CurrentProjectName = selected;
            return;
        }
        UpdateProjectActions();
        // 排除清单是全库共用的设置，与当前选中项目无关，只在页面首次载入时读一次。
        await LoadExcludeListAsync();
    }

    private void OnSharedSelectionChanged(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(async () => await ApplySharedSelectionAsync());

    private async Task ApplySharedSelectionAsync()
    {

        // 清单不随项目切换重载，也不需要离页保存协商：它不是按项目的状态。
        UpdateProjectActions();
        await Task.CompletedTask;
    }

    private void OnNewProjectNameChanged(object sender, TextChangedEventArgs e)
        => UpdateProjectActions();

    private void OnCommitMessageChanged(object sender, TextChangedEventArgs e)
        => UpdateProjectActions();

    private void OnSelectedProjectNameChanged(object sender, TextChangedEventArgs e)
        => UpdateRenameAction();

    private void OnOperationModeChanged(object sender, System.Windows.RoutedEventArgs e)
        => UpdateProjectActions();

    // 三段同行切换，恰有一个面板可见。
    private void OnBottomPageChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (RulePanel == null || HistoryPanel == null || GitHubPanel == null)
            return;
        RulePanel.Visibility = Visible(RulesPageButton);
        HistoryPanel.Visibility = Visible(HistoryPageButton);
        GitHubPanel.Visibility = Visible(GitHubPageButton);
    }

    private static Visibility Visible(System.Windows.Controls.Primitives.ToggleButton button)
        => button.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private async void OnCreateClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_busAccessor() is not { } bus
            || NewProjectName() is not { Length: > 0 } name
            || CurrentProjectName() is not { Length: > 0 } baseProject)
            return;
        var command = $"janus.proj.create name={CommandParser.QuoteArg(name)} " +
                      $"base={CommandParser.QuoteArg(baseProject)}";
        var result = await bus.ExecuteAsync(command, "UI");
        if (result.Success)
        {
            NewProjectNameBox.Clear();
            await RefreshProjectsAsync(name);
        }
    }

    private async void OnRenameClick(object sender, System.Windows.RoutedEventArgs e)
    {
        var current = CurrentProjectName();
        var target = SelectedProjectNameBox.Text.Trim();
        if (_busAccessor() is not { } bus || current.Length == 0 || target.Length == 0)
            return;
        if (!await SaveRulesOnPageLeaveAsync())
            return;

        SetProjectOperationRunning(true);
        try
        {
            var result = await bus.ExecuteAsync(
                $"janus.proj.rename name={CommandParser.QuoteArg(current)} " +
                $"new={CommandParser.QuoteArg(target)}", "UI");
            if (result.Success)
            {
                _selection.CurrentProjectName = target;
                await RefreshProjectsAsync(target);
            }
        }
        finally
        {
            SetProjectOperationRunning(false);
        }
    }

    private async void OnCommitSelectedClick(object sender, System.Windows.RoutedEventArgs e)
    {
        var mode = CurrentOperationMode();
        var project = CurrentProjectName();
        if (_busAccessor() is not { } bus
            || SelectedCommitMessageBox.Text.Trim() is not { Length: > 0 } message
            || ProjectOperationCommandBuilder.RequiresCurrentProject(mode) && project.Length == 0)
            return;

        SetProjectOperationRunning(true);
        try
        {
            await bus.ExecuteAsync(ProjectOperationCommandBuilder.BuildCommit(
                mode, project, message), "UI");
        }
        finally
        {
            SetProjectOperationRunning(false);
        }
    }

    private async void OnPushSelectedClick(object sender, System.Windows.RoutedEventArgs e)
    {
        var mode = CurrentOperationMode();
        var project = CurrentProjectName();
        if (_busAccessor() is not { } bus
            || ProjectOperationCommandBuilder.RequiresCurrentProject(mode) && project.Length == 0)
            return;

        SetProjectOperationRunning(true);
        try
        {
            await bus.ExecuteAsync(ProjectOperationCommandBuilder.BuildPush(
                mode, project), "UI");
        }
        finally
        {
            SetProjectOperationRunning(false);
        }
    }

    private string NewProjectName() => NewProjectNameBox.Text.Trim();

    private string CurrentProjectName() => _selection.CurrentProjectName?.Trim() ?? "";

    private void UpdateProjectActions()
    {
        var hasCurrent = CurrentProjectName().Length > 0;
        var mode = CurrentOperationMode();
        var currentMode = ProjectOperationCommandBuilder.RequiresCurrentProject(mode);
        var scopeReady = !currentMode || hasCurrent;
        if (!string.Equals(_nameBoxProject, CurrentProjectName(), StringComparison.OrdinalIgnoreCase))
        {
            _nameBoxProject = CurrentProjectName();
            SelectedProjectNameBox.Text = CurrentProjectName();
        }
        SelectedProjectNameBox.IsEnabled = !_projectOperationRunning && currentMode && hasCurrent;
        UpdateRenameAction();
        SelectedCommitButton.IsEnabled = !_projectOperationRunning && scopeReady
                                         && SelectedCommitMessageBox.Text.Trim().Length > 0;
        SelectedPushButton.IsEnabled = !_projectOperationRunning && scopeReady;
        SelectedCommitMessageBox.IsEnabled = !_projectOperationRunning;
        CurrentSubmodulesModeButton.IsEnabled = !_projectOperationRunning;
        CurrentBothModeButton.IsEnabled = !_projectOperationRunning;
        AllSubmodulesModeButton.IsEnabled = !_projectOperationRunning;
        AllBothModeButton.IsEnabled = !_projectOperationRunning;
        CreateProjectButton.IsEnabled = !_projectOperationRunning && hasCurrent && NewProjectName().Length > 0;
    }

    private void UpdateRenameAction()
    {
        if (RenameProjectButton == null || SelectedProjectNameBox == null)
            return;
        var current = CurrentProjectName();
        var currentMode = ProjectOperationCommandBuilder.RequiresCurrentProject(CurrentOperationMode());
        RenameProjectButton.IsEnabled = !_projectOperationRunning && currentMode
                                        && current.Length > 0 && !_isProtected(current)
                                        && SelectedProjectNameBox.Text.Trim().Length > 0
                                        && !SelectedProjectNameBox.Text.Trim().Equals(
                                            current, StringComparison.OrdinalIgnoreCase);
    }

    private void SetProjectOperationRunning(bool running)
    {
        _projectOperationRunning = running;
        UpdateProjectActions();
    }

    private ProjectOperationMode CurrentOperationMode()
    {
        if (CurrentSubmodulesModeButton.IsChecked == true)
            return ProjectOperationMode.CurrentSubmodules;
        if (AllSubmodulesModeButton.IsChecked == true)
            return ProjectOperationMode.AllSubmodules;
        if (AllBothModeButton.IsChecked == true)
            return ProjectOperationMode.AllBoth;
        return ProjectOperationMode.CurrentBoth;
    }

}
