using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using HistoryVulcan.Core.Commands;
using HistoryJanus.Git;

namespace HistoryJanus.Views;

/// <summary>独立窗口提交图谱：父边拓扑泳道 + 可视窗口裁剪。</summary>
public partial class GraphView : UserControl
{
    private const int PageLimit = 200;
    private const double CullPad = 96;

    private readonly Func<CommandBus?> _busAccessor;
    private readonly ProjectSelectionState _selection;
    private readonly DoubleCollection _mergeDash = new() { 4, 3 };
    private readonly DebouncedAction _selectionLoad;
    private GraphLayout.Result? _layout;
    private GraphSummary? _summary;
    private string? _loadedProject;
    private long _loadVersion;
    private bool _rendering;
    private bool _panning;
    private Point _panOrigin;
    private double _panOffsetX;
    private double _panOffsetY;

    public GraphView(Func<CommandBus?> busAccessor, ProjectSelectionState selection)
    {
        InitializeComponent();
        _busAccessor = busAccessor;
        _selection = selection;
        _selectionLoad = new DebouncedAction(() => _ = LoadSelectionAsync());
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

    private async Task LoadSelectionAsync()
    {
        var project = _selection.CurrentProjectName;
        if (project == null)
        {
            ShowPlaceholder("在项目总览中选择编号项目以查看提交图谱");
            return;
        }

        if (_layout != null &&
            string.Equals(_loadedProject, project, StringComparison.OrdinalIgnoreCase))
            return;

        if (_busAccessor() is not { } bus)
            return;

        var version = ++_loadVersion;
        CaptionText.Text = project;
        PlaceholderText.Text = "正在读取提交图谱...";
        PlaceholderText.Visibility = Visibility.Visible;
        Viewport.Visibility = Visibility.Collapsed;

        try
        {
            var quoted = CommandParser.QuoteArg(project);
            var commitsResult = await bus.ExecuteAsync(
                $"janus.graph.commits name={quoted} limit={PageLimit}", "UI");
            if (version != _loadVersion || !IsLoaded || !IsCurrentProject(project))
                return;
            if (IsCancelled(commitsResult))
                return;
            if (!commitsResult.Success ||
                !ModuleResultData.TryRead(commitsResult.Data, out GraphCommitsReport? report) ||
                report == null)
            {
                ShowPlaceholder(string.IsNullOrWhiteSpace(commitsResult.Message)
                    ? "图谱读取失败"
                    : commitsResult.Message);
                return;
            }

            if (report.Nodes == null || report.Nodes.Count == 0)
            {
                ShowPlaceholder("该项目暂无提交节点", BuildCaption(project, report, null));
                return;
            }

            _summary = null;
            _loadedProject = project;
            _layout = GraphLayout.Arrange(report, project);
            CaptionText.Text = BuildCaption(project, report, null);
            ApplyLayoutSize();
            PlaceholderText.Visibility = Visibility.Collapsed;
            Viewport.Visibility = Visibility.Visible;
            var scrollVersion = version;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (scrollVersion != _loadVersion || _layout == null)
                    return;
                Viewport.ScrollToHorizontalOffset(
                    Math.Max(0, _layout.Width - Viewport.ViewportWidth));
                RenderVisible();
            }));
            RenderVisible();
        }
        catch (OperationCanceledException)
        {
            // 项目切换或窗口卸载；旧结果不得覆盖当前选择。
        }
    }

    private bool IsCurrentProject(string project)
        => string.Equals(_selection.CurrentProjectName, project, StringComparison.OrdinalIgnoreCase);

    private static string BuildCaption(string project, GraphCommitsReport report, GraphSummary? summary)
    {
        var nodes = report.Nodes?.Count ?? 0;
        var lanes = report.Lanes?.Count ?? 0;
        if (summary == null)
            return $"{project} · {lanes} 条泳道 · {nodes} 个节点";
        var head = string.IsNullOrWhiteSpace(summary.HeadShortSha) ? "-" : summary.HeadShortSha;
        var dirty = summary.IsDirty
            ? (string.IsNullOrWhiteSpace(summary.DirtyMessage) ? "工作树有未提交变更" : summary.DirtyMessage)
            : "工作树干净";
        return $"{project} · HEAD {head} · {dirty} · {lanes} 条泳道 · {nodes} 个节点";
    }

    private static bool IsCancelled(CommandResult result)
        => !result.Success && result.Message.Contains("指令已取消", StringComparison.Ordinal);

    private void ShowPlaceholder(string message, string? caption = null)
    {
        _layout = null;
        _summary = null;
        _loadedProject = null;
        GraphCanvas.Children.Clear();
        GraphCanvas.Width = 1;
        GraphCanvas.Height = 1;
        CaptionText.Text = caption ?? "";
        PlaceholderText.Text = message;
        PlaceholderText.Visibility = Visibility.Visible;
        Viewport.Visibility = Visibility.Collapsed;
    }

    private void ApplyLayoutSize()
    {
        if (_layout == null)
            return;
        GraphCanvas.Width = Math.Max(1, _layout.Width);
        GraphCanvas.Height = Math.Max(1, _layout.Height);
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        => RenderVisible();

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
        => RenderVisible();

    private void OnViewportPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        => e.Handled = true;

    private void OnViewportPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (HitGraphNode(e.OriginalSource as DependencyObject))
            return;
        _panning = true;
        _panOrigin = e.GetPosition(Viewport);
        _panOffsetX = Viewport.HorizontalOffset;
        _panOffsetY = Viewport.VerticalOffset;
        Viewport.CaptureMouse();
        e.Handled = true;
    }

    private void OnViewportPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning)
            return;
        var now = e.GetPosition(Viewport);
        Viewport.ScrollToHorizontalOffset(_panOffsetX - (now.X - _panOrigin.X));
        Viewport.ScrollToVerticalOffset(_panOffsetY - (now.Y - _panOrigin.Y));
        e.Handled = true;
    }

    private void OnViewportPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => EndPan();

    private void OnViewportLostMouseCapture(object sender, MouseEventArgs e)
        => EndPan();

    private void EndPan()
    {
        if (!_panning)
            return;
        _panning = false;
        if (Viewport.IsMouseCaptured)
            Viewport.ReleaseMouseCapture();
    }

    private static bool HitGraphNode(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is FrameworkElement { Tag: string })
                return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void RenderVisible()
    {
        if (_rendering || _layout == null)
            return;
        _rendering = true;
        try
        {
            var viewW = Viewport.ViewportWidth > 0 ? Viewport.ViewportWidth : _layout.Width;
            var viewH = Viewport.ViewportHeight > 0 ? Viewport.ViewportHeight : _layout.Height;
            var viewX = Viewport.HorizontalOffset - CullPad;
            var viewY = Viewport.VerticalOffset - CullPad;
            viewW += CullPad * 2;
            viewH += CullPad * 2;

            GraphCanvas.Children.Clear();
            foreach (var edge in _layout.Edges)
            {
                var x = Math.Min(edge.X1, edge.X2) - 2;
                var y = Math.Min(edge.Y1, edge.Y2) - 2;
                var w = Math.Abs(edge.X2 - edge.X1) + 4;
                var h = Math.Abs(edge.Y2 - edge.Y1) + 4;
                if (!GraphLayout.Intersects(x, y, w, h, viewX, viewY, viewW, viewH))
                    continue;
                GraphCanvas.Children.Add(CreateEdge(edge));
            }

            foreach (var node in _layout.Nodes)
            {
                if (!GraphLayout.Intersects(
                        node.X, node.Y, GraphLayout.NodeWidth, GraphLayout.NodeHeight,
                        viewX, viewY, viewW, viewH))
                    continue;
                GraphCanvas.Children.Add(CreateNode(node));
                if (node.IsOpenTip)
                    GraphCanvas.Children.Add(CreateOpenTip(node));
            }
        }
        finally
        {
            _rendering = false;
        }
    }

    private Line CreateEdge(GraphLayout.PlacedEdge edge)
    {
        var line = new Line
        {
            X1 = edge.X1,
            Y1 = edge.Y1,
            X2 = edge.X2,
            Y2 = edge.Y2,
            StrokeThickness = edge.Edge.IsMergeParent ? 1.1 : 1.4,
            StrokeDashArray = edge.Edge.IsMergeParent ? _mergeDash : null,
            IsHitTestVisible = false,
        };
        line.SetResourceReference(
            Shape.StrokeProperty,
            edge.Edge.IsMergeParent ? "Aurora.Brush.TextSecondary" : "Aurora.Brush.Accent");
        return line;
    }

    private Border CreateNode(GraphLayout.PlacedNode placed)
    {
        var node = placed.Node;
        var isHead = _summary != null &&
                     node.Sha.Equals(_summary.HeadSha, StringComparison.OrdinalIgnoreCase);
        var isOpenTip = placed.IsOpenTip && !isHead;
        var border = new Border
        {
            Width = GraphLayout.NodeWidth,
            Height = GraphLayout.NodeHeight,
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(isHead || isOpenTip ? 2 : 1),
            Padding = new Thickness(6, 3, 6, 3),
            Cursor = Cursors.Hand,
            Tag = node.Sha,
            ClipToBounds = true,
            ToolTip = $"{ShortSha(node)}  {node.Subject}\n{node.CommittedAt:yyyy-MM-dd HH:mm}  {node.Author}",
        };
        border.SetResourceReference(
            Border.BackgroundProperty,
            isHead || isOpenTip ? "Aurora.Brush.AccentSoft" : "Aurora.Brush.SurfaceAlt");
        border.SetResourceReference(
            Border.BorderBrushProperty,
            isHead || isOpenTip ? "Aurora.Brush.Accent" : "Aurora.Brush.ControlBorder");

        var sha = new TextBlock
        {
            Text = ShortSha(node),
            FontWeight = FontWeights.SemiBold,
            FontSize = 11,
        };
        sha.SetResourceReference(TextBlock.ForegroundProperty, "Aurora.Brush.TextPrimary");
        var subject = new TextBlock
        {
            Text = Truncate(node.Subject, 18),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        subject.SetResourceReference(TextBlock.ForegroundProperty, "Aurora.Brush.TextSecondary");
        var stack = new StackPanel();
        stack.Children.Add(sha);
        stack.Children.Add(subject);
        border.Child = stack;
        border.MouseLeftButtonUp += OnNodeMouseUp;
        Canvas.SetLeft(border, placed.X);
        Canvas.SetTop(border, placed.Y);
        return border;
    }

    private Ellipse CreateOpenTip(GraphLayout.PlacedNode placed)
    {
        const double size = 8;
        var tip = new Ellipse
        {
            Width = size,
            Height = size,
            IsHitTestVisible = false,
        };
        tip.SetResourceReference(Shape.FillProperty, "Aurora.Brush.Accent");
        Canvas.SetLeft(tip, placed.X + GraphLayout.NodeWidth + 4);
        Canvas.SetTop(tip, placed.Y + GraphLayout.NodeHeight / 2 - size / 2);
        return tip;
    }

    private async void OnNodeMouseUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { Tag: string sha } ||
            _loadedProject is not { } project ||
            _busAccessor() is not { } bus)
            return;
        var result = await bus.ExecuteAsync(
            $"janus.graph.node name={CommandParser.QuoteArg(project)} sha={CommandParser.QuoteArg(sha)}",
            "UI");
        if (!IsCurrentProject(project) ||
            !result.Success ||
            !ModuleResultData.TryRead(result.Data, out GraphNodeDetail? detail) ||
            detail?.Node == null)
            return;

        var node = detail.Node;
        var files = FirstNonEmpty(detail.FileSummary, node.FileSummary);
        var body = new StringBuilder()
            .AppendLine($"提交：{node.Sha}")
            .AppendLine($"短名：{ShortSha(node)}")
            .AppendLine($"作者：{node.Author}")
            .AppendLine($"时间：{node.CommittedAt:yyyy-MM-dd HH:mm:ss}")
            .AppendLine($"标题：{node.Subject}")
            .AppendLine()
            .AppendLine("文件摘要：")
            .AppendLine(string.IsNullOrWhiteSpace(files) ? "(无)" : files)
            .AppendLine()
            .AppendLine("差异摘要：")
            .AppendLine(string.IsNullOrWhiteSpace(detail.DiffSummary) ? "(无)" : detail.DiffSummary);
        await AuroraDialog.ShowContentAsync(
            bus,
            $"提交 {ShortSha(node)}",
            string.IsNullOrWhiteSpace(result.Message) ? node.Subject : result.Message,
            body.ToString());
    }

    private static string ShortSha(GraphCommitNode node)
        => string.IsNullOrWhiteSpace(node.ShortSha)
            ? (node.Sha.Length <= 10 ? node.Sha : node.Sha[..10])
            : node.ShortSha;

    private static string FirstNonEmpty(string left, string right)
        => !string.IsNullOrWhiteSpace(left) ? left : right;

    private static string Truncate(string text, int max)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }
}
