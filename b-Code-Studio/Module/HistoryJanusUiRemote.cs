using HistoryVulcan.Core.Commands;

namespace HistoryJanus.Module;

internal static partial class HistoryJanusUiCommands
{
    // 页面只负责收集输入、调用带确认门的业务命令和刷新实际结果。
    private static void RegisterRemoteCommand(ICommandRegistrar registry, ICommandBus bus)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "janus.ui.setremote",
            Domain = Domain,
            CommandClass = "ui",
            Summary = "保存选中项目的 origin 拉取与推送地址并刷新 GitHub 子页面",
            Example = "janus.ui.setremote name=2026-041-VideoStudio url=https://github.com/Cloudsflee/video-studio",
            HiddenReason = "界面内部协议；模型请用 janus.github.remote",
            Parameters =
            [
                new ParameterSpec { Name = "name", Description = "已登记项目名（目录名）", Required = true },
                new ParameterSpec { Name = "url", Description = "origin 仓库地址，拉取与推送共用", Required = true },
            ],
            Handler = async context =>
            {
                var name = context.RequireString("name");
                var url = context.RequireString("url");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                    return CommandResult.Fail("请选择项目并填写远端地址");
                var result = await bus.ExecuteAsync(
                    $"janus.github.remote name={Quote(name)} fetch={Quote(url)} push={Quote(url)} apply=true",
                    context.Source, context.Cancellation);
                if (!result.Success)
                    return result;
                await bus.ExecuteAsync("aurora.ui.refreshdata node=github-rows", context.Source, context.Cancellation);
                return result;
            },
        });
    }
}
