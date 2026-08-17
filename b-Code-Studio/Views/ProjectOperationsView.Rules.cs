using System.Windows;
using HistoryVulcan.Core.Commands;

namespace HistoryJanus.Views;

/// <summary>
/// 规则分段：全库共用的「不纳入仓库」清单。
///
/// 4.x 这里是一张十列三态规则表（Git/LFS/LF × 每个格式 × 每个项目），
/// 外加延迟保存、离页确认和格式台账缓存共约 500 行。
/// 5.0.0 起清单全库只有一份、只有"要不要进仓库"一个维度，所以既不需要按项目加载，
/// 也不需要离页保存协商——页面只是设置的一个编辑器。
/// </summary>
public partial class ProjectOperationsView
{
    private bool _ruleOperationRunning;

    private async Task LoadExcludeListAsync()
    {
        var bus = _busAccessor();
        if (bus == null)
            return;
        var result = await bus.ExecuteAsync("janus.gitrule.list", "ProjectOperations");
        if (!result.Success)
        {
            RuleStatusText.Text = $"读取清单失败：{result.Message}";
            return;
        }
        if (result.Data is ExcludeRuleReportView report)
        {
            ExcludeListBox.Text = string.Join(", ",
                report.Directories.Concat(report.Suffixes));
        }
        RuleStatusText.Text = result.Message;
    }

    private async void OnRefreshRulesClick(object sender, RoutedEventArgs e)
        => await LoadExcludeListAsync();

    private async void OnSaveExcludesClick(object sender, RoutedEventArgs e)
        => await SaveExcludeListAsync(ExcludeListBox.Text);

    /// <summary>
    /// 保存清单。返回 null 表示页面自己就拒了（空清单），没有发出任何命令。
    /// 单独成一个可 await 的方法，是为了让 Smoke 能确定性地断言总线调用，
    /// 而不必对 async void 处理器泵 WPF 消息队列。
    /// </summary>
    internal async Task<CommandResult?> SaveExcludeListAsync(string? raw)
    {
        if (_ruleOperationRunning)
            return null;
        var bus = _busAccessor();
        if (bus == null)
            return null;
        var list = raw?.Trim();
        if (string.IsNullOrEmpty(list))
        {
            RuleStatusText.Text = "清单不能为空";
            return null;
        }

        _ruleOperationRunning = true;
        SaveExcludesButton.IsEnabled = false;
        try
        {
            // 写入走命令总线，确认策略由宿主执行——页面不自己弹确认，也不直接写设置。
            var result = await bus.ExecuteAsync(
                ProjectOperationCommandBuilder.Excludes(list), "ProjectOperations");
            RuleStatusText.Text = result.Message;
            if (result.Success)
                await LoadExcludeListAsync();
            return result;
        }
        finally
        {
            _ruleOperationRunning = false;
            SaveExcludesButton.IsEnabled = true;
        }
    }
}

/// <summary>
/// 跨宿主 HTTP 边界后结构化结果可能是 JsonElement，页面只取自己要显示的两组清单。
/// </summary>
public sealed record ExcludeRuleReportView(
    IReadOnlyList<string> Suffixes,
    IReadOnlyList<string> Directories);
