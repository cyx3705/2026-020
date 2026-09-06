using System.IO;
using System.Text.Json;
using HistoryJanus.Git;
using HistoryVulcan.Core.Commands;

namespace HistoryJanus.Module;

internal static partial class HistoryJanusUiCommands
{
    private static void RegisterLifecycleCommands(
        CommandRegistry registry,
        CommandBus bus,
        string source,
        Func<StudioBusinessComposition?> business)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "janus.ui.refreshprojects",
            Domain = Domain,
            CommandClass = "ui",
            Summary = "查询远端并重取项目总览",
            HiddenReason = "本机界面动作，不进入 MCP",
            // 先立「这一次要付全价」的旗，再让表格重取。
            // 表格的取数参数里不能写 refresh：那一格现在由搜索词与年份占着，
            // 而它们一变就重取——每敲一个字符 fetch 一轮远端是不能接受的。
            Handler = context =>
            {
                RequestProjectRefresh();
                return bus.ExecuteAsync(
                    "aurora.ui.refreshdata node=projects", context.Source, context.Cancellation);
            },
        }, source);
        registry.Register(new CommandDescriptor
        {
            Name = "janus.ui.projectaction",
            Domain = Domain,
            CommandClass = "ui",
            Summary = "执行项目总览状态单元格对应动作",
            HiddenReason = "本机界面动作，不进入 MCP",
            Parameters =
            [
                new ParameterSpec { Name = "name", Description = "项目名", Required = true, Position = 0 },
                new ParameterSpec { Name = "action", Description = "状态动作", Required = true, Position = 1 },
            ],
            Handler = context => ProjectActionAsync(context, bus, business),
        }, source);
        registry.Register(new CommandDescriptor
        {
            Name = "janus.ui.openmeta",
            Domain = Domain,
            CommandClass = "ui",
            Summary = "打开项目直属 z/Z 文件夹；多个时先选择",
            HiddenReason = "本机界面动作，不进入 MCP",
            Parameters = [new ParameterSpec { Name = "name", Description = "项目名", Required = true, Position = 0 }],
            Handler = context => OpenMetaAsync(context, bus, business),
        }, source);
    }

    private static async Task<CommandResult> ProjectActionAsync(
        CommandContext context, CommandBus bus, Func<StudioBusinessComposition?> business)
    {
        var name = context.RequireString("name").Trim();
        var action = context.RequireString("action").Trim();
        var projects = business()?.Projects;
        if (projects == null)
            return CommandResult.Fail("Janus 项目服务尚未就绪");
        var current = await projects.RefreshProjectAsync(name, true, context.Cancellation);
        if (!current.Action.Equals(action, StringComparison.Ordinal))
        {
            _ = await bus.ExecuteAsync("aurora.ui.refreshdata node=projects", context.Source, context.Cancellation);
            return CommandResult.Fail($"项目状态已变化：当前应执行“{current.Action}”（{current.Message}）");
        }

        string command;
        if (action == "提交")
        {
            var dialog = await bus.ExecuteAsync(
                $"aurora.ui.dialog kind=prompt title=提交 body={Quote(name)} primary=提交 cancel=取消",
                context.Source, context.Cancellation);
            if (!dialog.Success) return dialog;
            var message = DialogValue(dialog);
            if (message.Length == 0) return CommandResult.Fail("提交描述不能为空");
            command = $"janus.proj.commit name={Quote(name)} msg={Quote(message)}";
        }
        else
        {
            command = action switch
            {
                "推送" => $"janus.proj.push name={Quote(name)}",
                "同步" => $"janus.proj.sync name={Quote(name)}",
                "归档" => $"janus.proj.archive name={Quote(name)}",
                "拉取" => $"janus.proj.pull name={Quote(name)}",
                _ => "",
            };
            if (command.Length == 0) return CommandResult.Fail($"未知项目状态动作: {action}");
        }
        var result = await bus.ExecuteAsync(command, context.Source, context.Cancellation);
        if (result.Success)
            _ = await bus.ExecuteAsync("aurora.ui.refreshdata node=projects", context.Source, context.Cancellation);
        return result;
    }

    private static async Task<CommandResult> OpenMetaAsync(
        CommandContext context, CommandBus bus, Func<StudioBusinessComposition?> business)
    {
        var name = context.RequireString("name").Trim();
        var projects = business()?.Projects;
        if (projects == null) return CommandResult.Fail("Janus 项目服务尚未就绪");
        var root = Path.Combine(projects.LibraryRoot, name);
        var folders = ProjectService.ReadZFolderNames(root);
        if (folders.Count == 0) return CommandResult.Fail("该项目没有直属 z/Z 文件夹");
        var selected = folders[0];
        if (folders.Count > 1)
        {
            var options = JsonSerializer.Serialize(folders.Select(item => new { label = item, value = item }));
            var choice = await bus.ExecuteAsync(
                $"aurora.ui.dialog kind=choice title={Quote(name)} body=选择z级文件夹 options={Quote(options)} primary=打开 cancel=取消",
                context.Source, context.Cancellation);
            if (!choice.Success) return choice;
            selected = DialogValue(choice);
            if (!folders.Contains(selected, StringComparer.OrdinalIgnoreCase))
                return CommandResult.Fail("选择的 z/Z 文件夹已不存在");
        }
        var result = await projects.OpenMetaFolderAsync(Path.Combine(root, selected), null, null);
        return result.Success ? CommandResult.Ok(result.Message) : CommandResult.Fail(result.Message);
    }

    private static string DialogValue(CommandResult result) => result.Data switch
    {
        string value => value.Trim(),
        JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString()?.Trim() ?? "",
        _ => result.Message.Trim(),
    };
}
