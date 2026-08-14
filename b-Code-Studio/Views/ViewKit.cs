using System.Windows;
using System.Windows.Threading;
using HistoryVulcan.Core.Commands;

namespace HistoryJanus.Views;

/// <summary>
/// 视图通用小件。
/// 说明:未做视图基类——WPF 要求 XAML 根元素类型与代码后置基类一致,
/// 使用组合式助手，避免为共享小功能改动所有 XAML 根元素。
/// </summary>
internal static class ViewKit
{
    public static string ResultSummary(CommandResult result)
        => result.Message.Split('\n')[0];

    /// <summary>
    /// Loaded 首次触发时执行一次(停靠重排会反复触发 Loaded,守卫防重复加载)。
    /// 适用于"标志只作守卫"的视图;标志兼作加载状态位的视图(如搜索框联动)自持字段。
    /// </summary>
    public static void RunOnceOnLoaded(FrameworkElement view, Func<Task> action)
    {
        var done = false;
        view.Loaded += async (_, _) =>
        {
            if (done)
                return;
            done = true;
            await action();
        };
    }
}

/// <summary>
/// 把连续的选中/Loaded 合成一次加载，避免取消正在进行的宿主 HTTP 请求。
/// </summary>
internal sealed class DebouncedAction
{
    public const int DefaultDelayMs = 280;
    private readonly DispatcherTimer _timer;

    public DebouncedAction(Action action, int milliseconds = DefaultDelayMs)
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            action();
        };
    }

    public void Schedule()
    {
        _timer.Stop();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();
}
