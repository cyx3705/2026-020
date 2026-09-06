using System.Text.Json;
using static HistoryJanus.Smoke.SmokeKit;

namespace HistoryJanus.Smoke.Suites;

/// <summary>
/// 5.4.4 架构门禁：Janus 通过宿主命令总线提供页面描述，Aurora 负责 WPF 构造。
/// 这组断言防止旧的宿主停靠接口在迁移后悄悄回流。
/// </summary>
internal static class TestArchitectureSuite
{
    public static Task RunAsync(string[] args)
    {
        var module = File.ReadAllText(Path.Combine(RepoRoot, "Module", "HistoryJanusUiModule.cs"));
        var project = File.ReadAllText(Path.Combine(RepoRoot, "Module", "HistoryJanus.Module.csproj"));
        var manifest = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(RepoRoot, "Module", "module.manifest.json"))).RootElement;

        True(module.Contains("IModuleContextAware", StringComparison.Ordinal),
            "module uses the HistoryVulcan 5.1 context contract");
        foreach (var forbidden in new[]
        {
            "IUiModule", "IShellUiAware", "IShellUiRegistrar", "ToolWindowDescriptor",
            "context.Settings", "context.Log", "context.DataDirectory", "HistoryVulcan.Extensibility",
        })
        {
            True(!module.Contains(forbidden, StringComparison.Ordinal)
                 && !project.Contains(forbidden, StringComparison.Ordinal),
                $"legacy host surface is absent: {forbidden}");
        }

        foreach (var command in new[] { "janus.ui.describe", "janus.ui.actions", "janus.ui.data" })
            Contains(module, command, $"descriptive frontend command is registered: {command}");

        Equal("5.6.0", manifest.GetProperty("version").GetString(), "module manifest is 5.6.0");
        True(manifest.GetProperty("ui").GetBoolean(), "module advertises a frontend surface");
        True(File.Exists(Path.Combine(RepoRoot, "Module", "HistoryJanus.Module.csproj")),
            "module project remains the package source");
        return Task.CompletedTask;
    }
}
