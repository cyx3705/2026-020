using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HistoryVulcan.Core.Commands;
using HistoryJanus.Git;

namespace HistoryJanus.Views;

/// <summary>跟随共享项目选择的分支提交历史窗口。</summary>
public partial class BranchHistoryView : UserControl
{
    private const int PageSize = 200;

    private readonly Func<CommandBus?> _busAccessor;
    private readonly ProjectSelectionState _selection;
    private readonly Func<string, bool> _isProtected;
    private readonly DebouncedAction _selectionLoad;
    private BranchHistoryReport? _report;
    private int _loadedOwnCommits;
    private long _loadVersion;

    public BranchHistoryView(
        Func<CommandBus?> busAccessor,
        ProjectSelectionState selection,
        Func<string, bool> isProtected)
    {
        InitializeComponent();
        _busAccessor = busAccessor;
        _selection = selection;
        _isProtected = isProtected;
        _selectionLoad = new DebouncedAction(() => _ = LoadSelectionAsync(resetLimit: true, reuseCurrent: true));
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _selection.Changed -= OnProjectSelectionChanged;
        _selection.Changed += OnProjectSelectionChanged;
        _selectionLoad.Schedule();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _selection.Changed -= OnProjectSelectionChanged;
        _selectionLoad.Stop();
    }

    private void OnProjectSelectionChanged(object? sender, EventArgs e)
        => _selectionLoad.Schedule();

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
        => await LoadSelectionAsync(resetLimit: true);

    private async void OnRemoteRefreshClick(object sender, RoutedEventArgs e)
        => await LoadSelectionAsync(resetLimit: true, refreshRemote: true);

    private async void OnLoadMoreClick(object sender, RoutedEventArgs e)
        => await LoadSelectionAsync(resetLimit: false, loadEarlier: true);

    private async Task LoadSelectionAsync(
        bool resetLimit,
        bool refreshRemote = false,
        bool loadEarlier = false,
        bool reuseCurrent = false)
    {
        var project = _selection.CurrentProjectName;
        if (project == null)
        {
            Clear("尚未选择项目");
            return;
        }
        if (_busAccessor() is not { } bus)
            return;
        if (reuseCurrent
            && resetLimit
            && !refreshRemote
            && !loadEarlier
            && _report != null
            && string.Equals(_report.Branch, project, StringComparison.OrdinalIgnoreCase))
            return;

        if (resetLimit)
            _loadedOwnCommits = 0;
        var skip = loadEarlier ? _loadedOwnCommits : 0;

        var version = ++_loadVersion;

        SetBusy(true);
        BoundaryText.Text = refreshRemote ? "正在刷新远端并读取历史..." : "正在读取分支历史...";
        try
        {
            var command = $"janus.history.list name={CommandParser.QuoteArg(project)} " +
                          $"limit={PageSize} skip={skip}" +
                          (refreshRemote ? " remote=true" : "");
            var result = await bus.ExecuteAsync(command, "UI");
            if (version != _loadVersion || !IsLoaded || !IsCurrentProject(project))
                return;
            if (!result.Success && result.Message.Contains("指令已取消", StringComparison.Ordinal))
                return;
            if (!result.Success || !ModuleResultData.TryRead(result.Data, out BranchHistoryReport? report))
            {
                _report = null;
                HistoryList.ItemsSource = null;
                BoundaryText.Text = "历史读取失败";
                return;
            }

            BranchHistoryEntry? scrollAnchor = null;
            if (loadEarlier && _report?.Branch.Equals(report.Branch,
                    StringComparison.OrdinalIgnoreCase) == true)
            {
                var baseline = report.Entries[0];
                var mergedOwn = report.Entries.Skip(1)
                    .Concat(_report.Entries.Skip(1))
                    .DistinctBy(entry => entry.Sha, StringComparer.Ordinal)
                    .ToList();
                scrollAnchor = _report.Entries.Skip(1).FirstOrDefault();
                _report = report with { Entries = [baseline, .. mergedOwn] };
            }
            else
            {
                _report = report;
            }

            _loadedOwnCommits = Math.Max(0, _report.Entries.Count - 1);
            BoundaryText.Text = $"父分支：{_report.ParentDisplay}";
            HistoryList.ItemsSource = _report.Entries;
            LoadMoreButton.IsEnabled = _report.HasMore;
            if (scrollAnchor != null)
                HistoryList.ScrollIntoView(scrollAnchor);
            else if (_report.Entries.Count > 0)
            {
                // 虚拟化列表需等布局完成后滚动才可靠；版本号防止旧结果覆盖新选择
                var last = _report.Entries[^1];
                var scrollVersion = version;
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    if (scrollVersion == _loadVersion && HistoryList.Items.Count > 0)
                        HistoryList.ScrollIntoView(last);
                }));
            }
        }
        catch (OperationCanceledException)
        {
            // 项目切换或窗口卸载；旧结果不得覆盖当前选择。
        }
        finally
        {
            if (version == _loadVersion)
                SetBusy(false);
        }
    }

    private bool IsCurrentProject(string project)
        => string.Equals(_selection.CurrentProjectName, project, StringComparison.OrdinalIgnoreCase);

    private void SetBusy(bool busy)
    {
        RefreshButton.IsEnabled = !busy && _selection.CurrentProjectName != null;
        RemoteRefreshButton.IsEnabled = !busy && _selection.CurrentProjectName != null;
        if (busy)
            LoadMoreButton.IsEnabled = false;
    }

    private void Clear(string status)
    {
        _report = null;
        _loadedOwnCommits = 0;
        HistoryList.ItemsSource = null;
        BoundaryText.Text = status;
        SetBusy(false);
        LoadMoreButton.IsEnabled = false;
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (HistoryList.SelectedItem is not BranchHistoryEntry || _report == null)
        {
            e.Handled = true;
            return;
        }
        HardResetItem.IsEnabled = !_isProtected(_report.Branch);
    }

    private void OnHistoryPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject);
        if (item != null)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private async void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
        => await ShowDetailAsync();

    private async void OnShowDetailClick(object sender, RoutedEventArgs e)
        => await ShowDetailAsync();

    private async Task ShowDetailAsync()
    {
        if (!TrySelection(out var branch, out var entry) || _busAccessor() is not { } bus)
            return;
        var result = await bus.ExecuteAsync(
            $"janus.history.show name={CommandParser.QuoteArg(branch)} sha={entry.Sha}", "UI");
        if (!result.Success || !ModuleResultData.TryRead(result.Data, out CommitDetail? detail))
            return;

        var body = new StringBuilder()
            .AppendLine($"提交：{detail.Sha}")
            .AppendLine($"作者：{detail.Author} <{detail.AuthorEmail}>")
            .AppendLine($"时间：{detail.TimeDisplay}")
            .AppendLine($"父提交：{(detail.Parents.Count == 0 ? "(无)" : string.Join(" ", detail.Parents))}")
            .AppendLine()
            .AppendLine(detail.Subject);
        if (detail.Body.Length > 0)
            body.AppendLine().AppendLine(detail.Body);
        body.AppendLine().AppendLine($"变更文件（{detail.Files.Count}）：");
        foreach (var file in detail.Files)
            body.AppendLine($"{file.Status,-5} {file.DisplayPath}");
        ShowPreview($"提交详情 {detail.ShortSha}", result.Message, body.ToString());
    }

    private async void OnShowDiffClick(object sender, RoutedEventArgs e)
        => await ShowDiffAsync();

    private async Task ShowDiffAsync()
    {
        if (!TrySelection(out var branch, out var entry) || _busAccessor() is not { } bus)
            return;
        var result = await bus.ExecuteAsync(
            $"janus.history.diff name={CommandParser.QuoteArg(branch)} sha={entry.Sha}", "UI");
        if (!result.Success || !ModuleResultData.TryRead(result.Data, out BranchDiffReport? report))
            return;
        var header = $"目标：{report.TargetSha}\n当前：{report.HeadSha}\n" +
                     $"提交：{report.CommitCount}  文件：{report.FileCount}\n{report.ShortStat}";
        ShowPreview($"差异预览 {entry.ShortSha} -> HEAD", header, report.DiffText);
    }

    private void OnCopyShaClick(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not BranchHistoryEntry entry)
            return;
        Clipboard.SetText(entry.Sha);
    }

    private async void OnRollbackClick(object sender, RoutedEventArgs e)
    {
        if (!TrySelection(out var branch, out var entry) || _busAccessor() is not { } bus)
            return;
        var dialog = new RollbackMessageDialog(entry.ShortSha, entry.Subject)
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() != true)
            return;
        var command = $"janus.history.rollback name={CommandParser.QuoteArg(branch)} sha={entry.Sha} " +
                      $"msg={CommandParser.QuoteArg(dialog.CommitMessage)}";
        var result = await bus.ExecuteAsync(command, "UI");
        if (result.Success)
            await LoadSelectionAsync(resetLimit: true);
    }

    private async void OnHardResetClick(object sender, RoutedEventArgs e)
    {
        if (!TrySelection(out var branch, out var entry) || _isProtected(branch) ||
            _busAccessor() is not { } bus)
            return;
        var result = await bus.ExecuteAsync(
            $"janus.history.reset name={CommandParser.QuoteArg(branch)} sha={entry.Sha}", "UI");
        if (result.Success)
            await LoadSelectionAsync(resetLimit: true);
    }

    private bool TrySelection(out string branch, out BranchHistoryEntry entry)
    {
        branch = _report?.Branch ?? "";
        entry = HistoryList.SelectedItem as BranchHistoryEntry ?? null!;
        return branch.Length > 0 && entry != null;
    }

    private void ShowPreview(string title, string summary, string content)
    {
        var dialog = new HistoryPreviewDialog(title, summary, content)
        {
            Owner = Window.GetWindow(this),
        };
        dialog.ShowDialog();
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source != null)
        {
            if (source is T match)
                return match;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }
}
