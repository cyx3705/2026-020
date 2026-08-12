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
    private readonly ObservableCollection<RuleEditRow> _rules = [];
    private List<string> _projectNames = [];
    private bool _projectOperationRunning;
    private bool _ruleOperationRunning;
    private string? _loadedRuleProject;
    private int _ruleLoadGeneration;

    public ProjectOperationsView(Func<CommandBus?> busAccessor, ProjectSelectionState selection,
        Func<string, bool> isProtected, Func<GitHubConnectionService?> gitHubAccessor)
    {
        InitializeComponent();
        _busAccessor = busAccessor;
        _selection = selection;
        HistoryPanel.Content = new BranchHistoryView(busAccessor, selection, isProtected);
        // GitHub 连接治理是本页第三个分段，不是宿主级独立窗口。
        GitHubPanel.Content = new GitHubConnectionView(gitHubAccessor, busAccessor);
        SelectedCommitMessageBox.Text = "一键推送更新";
        RuleGrid.ItemsSource = _rules;
        InitializeRuleDeferredSave();
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
        if (selected != null)
            await LoadRulesAsync(selected);
        else
            ClearRules();
    }

    private void OnSharedSelectionChanged(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(async () => await ApplySharedSelectionAsync());

    private async Task ApplySharedSelectionAsync()
    {
        var selected = _selection.CurrentProjectName is { } current
            ? _projectNames.FirstOrDefault(name => name.Equals(current, StringComparison.OrdinalIgnoreCase))
            : null;

        if (_loadedRuleProject is { Length: > 0 } loaded
            && !string.Equals(selected, loaded, StringComparison.OrdinalIgnoreCase)
            && !await SaveRulesOnPageLeaveAsync())
        {
            _selection.CurrentProjectName = loaded;
            return;
        }

        UpdateProjectActions();
        if (selected != null
            && !string.Equals(selected, _loadedRuleProject, StringComparison.OrdinalIgnoreCase))
            await LoadRulesAsync(selected);
        else
        {
            if (selected == null)
                ClearRules();
        }
    }

    private void OnNewProjectNameChanged(object sender, TextChangedEventArgs e)
        => UpdateProjectActions();

    private void OnCommitMessageChanged(object sender, TextChangedEventArgs e)
        => UpdateProjectActions();

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
        var scopeReady = !ProjectOperationCommandBuilder.RequiresCurrentProject(mode) || hasCurrent;
        CreateProjectButton.IsEnabled = hasCurrent && NewProjectName().Length > 0;
        SelectedActionTitle.Text = !ProjectOperationCommandBuilder.RequiresCurrentProject(mode)
            ? "全部工作树"
            : hasCurrent
            ? $"当前项目 · {CurrentProjectName()}"
            : "当前未选择项目";
        SelectedCommitButton.IsEnabled = !_projectOperationRunning && scopeReady
                                         && SelectedCommitMessageBox.Text.Trim().Length > 0;
        SelectedPushButton.IsEnabled = !_projectOperationRunning && scopeReady;
        SelectedCommitMessageBox.IsEnabled = !_projectOperationRunning;
        CurrentSubmodulesModeButton.IsEnabled = !_projectOperationRunning;
        CurrentBothModeButton.IsEnabled = !_projectOperationRunning;
        AllSubmodulesModeButton.IsEnabled = !_projectOperationRunning;
        AllBothModeButton.IsEnabled = !_projectOperationRunning;
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
