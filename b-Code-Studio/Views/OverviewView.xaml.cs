using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using HistoryVulcan.Core.Commands;
using HistoryJanus.Git;

namespace HistoryJanus.Views;

/// <summary>项目工作树、Z 级元文件夹与共享项目选择。</summary>
public partial class OverviewView : UserControl
{
    private readonly Func<CommandBus?> _busAccessor;
    private readonly ProjectSelectionState _selection;
    private List<WorktreeRow> _allRows = [];
    private bool _initialLoadDone;
    private bool _suppressSelection;

    public OverviewView(Func<CommandBus?> busAccessor, ProjectSelectionState selection)
    {
        InitializeComponent();
        _busAccessor = busAccessor;
        _selection = selection;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public sealed record WorktreeRow(
        int Index,
        string BranchName,
        string WorktreePath,
        string LastCommitMessage,
        bool HasNameMismatch,
        string FolderName,
        bool? IsClean,
        string WorktreeStatusMessage,
        IReadOnlyList<MetaFolderInfo> MetaFolders)
    {
        public MetaFolderInfo? PrimaryMeta => MetaFolders.FirstOrDefault();
        public string PrimaryMetaName => PrimaryMeta?.MetaName ?? "-";
        public bool HasMeta => PrimaryMeta != null;
        public bool HasAdditionalMeta => MetaFolders.Count > 1;
        public string MoreMetaLabel => HasAdditionalMeta ? $"+{MetaFolders.Count - 1}" : "";
        public string BranchDisplay => HasNameMismatch ? $"{BranchName} ⚠" : BranchName;
        public string BranchToolTip => HasNameMismatch
            ? $"当前 HEAD 不是 main，不阻断列举与提交。\n{WorktreePath}"
            : WorktreePath;
        public string CommitDisplay => string.IsNullOrWhiteSpace(LastCommitMessage) ? "-" : LastCommitMessage;
        public string CleanStatusGlyph => IsClean switch
        {
            true => "✓",
            false => "×",
            _ => "?",
        };
        public string CleanStatusToolTip => string.IsNullOrWhiteSpace(WorktreeStatusMessage)
            ? "工作树状态未知"
            : WorktreeStatusMessage;
    }

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        _selection.Changed -= OnSharedSelectionChanged;
        _selection.Changed += OnSharedSelectionChanged;
        if (!_initialLoadDone)
        {
            _initialLoadDone = true;
            await RefreshAsync();
        }
        else
        {
            ApplySharedSelection();
        }
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
        => _selection.Changed -= OnSharedSelectionChanged;

    private async void OnRefreshClick(object sender, System.Windows.RoutedEventArgs e)
        => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_busAccessor() is not { } bus)
            return;
        RefreshButton.IsEnabled = false;
        try
        {
            var projectsTask = bus.ExecuteAsync("janus.proj.list status=true", "UI");
            var metasTask = bus.ExecuteAsync("janus.proj.metas", "UI");
            await Task.WhenAll(projectsTask, metasTask);

            var projectsResult = await projectsTask;
            if (!projectsResult.Success ||
                !ModuleResultData.TryRead(projectsResult.Data, out List<WorktreeInfo>? projects))
                return;

            var metasResult = await metasTask;
            List<MetaFolderInfo>? loadedMetas = null;
            var metasLoaded = metasResult.Success &&
                              ModuleResultData.TryRead(metasResult.Data, out loadedMetas) &&
                              loadedMetas != null;
            List<MetaFolderInfo> metas = metasLoaded ? loadedMetas! : [];
            _allRows = OverviewMetaMerge.Merge(projects, metas);
            if (_selection.CurrentProjectName is { } current
                && !_allRows.Any(row => row.BranchName.Equals(current, StringComparison.OrdinalIgnoreCase)))
            {
                _selection.CurrentProjectName = null;
            }
            ApplyFilter();
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void ApplyFilter()
    {
        var keyword = SearchBox.Text.Trim();
        var rows = keyword.Length == 0
            ? _allRows
            : _allRows.Where(row => OverviewMetaMerge.MatchesKeyword(row, keyword)).ToList();
        WorktreeList.ItemsSource = rows;
        ApplySharedSelection();
    }

    private void ApplySharedSelection()
    {
        var rows = WorktreeList.ItemsSource as IEnumerable<WorktreeRow> ?? [];
        _suppressSelection = true;
        WorktreeList.SelectedItem = _selection.CurrentProjectName is { } current
            ? rows.FirstOrDefault(row => row.BranchName.Equals(current, StringComparison.OrdinalIgnoreCase))
            : null;
        _suppressSelection = false;
        if (WorktreeList.SelectedItem != null)
            WorktreeList.ScrollIntoView(WorktreeList.SelectedItem);
    }

    private void OnSharedSelectionChanged(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(ApplySharedSelection);

    private void OnProjectSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressSelection && WorktreeList.SelectedItem is WorktreeRow row)
            _selection.CurrentProjectName = row.BranchName;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_initialLoadDone)
            ApplyFilter();
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (WorktreeList.SelectedItem is WorktreeRow row)
            _ = _busAccessor()?.ExecuteAsync(
                $"janus.proj.open name={CommandParser.QuoteArg(row.BranchName)}", "UI");
    }

    private async void OnMetaFolderClick(object sender, System.Windows.RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: MetaFolderInfo meta })
            await OpenMetaFolderAsync(meta);
    }

    private void OnMoreMetaClick(object sender, System.Windows.RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { DataContext: WorktreeRow row } button)
            return;

        var menu = new ContextMenu
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
        };
        foreach (var meta in row.MetaFolders)
        {
            var item = new MenuItem
            {
                Header = meta.MetaName,
                ToolTip = meta.FullPath,
                Tag = meta,
            };
            item.Click += OnMetaMenuItemClick;
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private async void OnMetaMenuItemClick(object sender, System.Windows.RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: MetaFolderInfo meta })
            await OpenMetaFolderAsync(meta);
    }

    private async Task OpenMetaFolderAsync(MetaFolderInfo meta)
    {
        if (_busAccessor() is not { } bus)
            return;
        await bus.ExecuteAsync(OverviewMetaMerge.BuildOpenCommand(meta), "UI");
    }
}
