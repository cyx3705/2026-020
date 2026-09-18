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
                new ParameterSpec { Name = "msg", Description = "提交描述；省略时弹窗输入" },
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
        if (action == ProjectService.RefreshAction)
        {
            // 「刷新」本身就是上面那次带 fetch 的复核；把结果写回这一行即可。
            await RefreshCachedProjectAsync(projects, name, current, context.Cancellation);
            _ = await bus.ExecuteAsync("aurora.ui.refreshdata node=projects", context.Source, context.Cancellation);
            return current.State == ProjectLifecycleState.Unavailable
                ? CommandResult.Fail($"远端状态仍无法确认：{current.Message}")
                : CommandResult.Ok($"已刷新：当前应执行“{current.Action}”（{current.Message}）");
        }
        if (!current.Action.Equals(action, StringComparison.Ordinal))
        {
            await RefreshCachedProjectAsync(projects, name, current, context.Cancellation);
            _ = await bus.ExecuteAsync("aurora.ui.refreshdata node=projects", context.Source, context.Cancellation);
            return CommandResult.Fail($"项目状态已变化：当前应执行“{current.Action}”（{current.Message}）");
        }

        string command;
        if (action == "提交")
        {
            var decided = await DecideCommitAsync(context, bus, projects, name);
            if (decided.Result != null) return decided.Result;
            command = decided.Command!;
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
        {
            await RefreshCachedProjectAsync(projects, name, null, context.Cancellation);
            _ = await bus.ExecuteAsync("aurora.ui.refreshdata node=projects", context.Source, context.Cancellation);
        }
        return result;
    }

    /// <summary>提交动作里「丢弃」那一支的回执值。</summary>
    private const string DiscardChoice = "discard";

    /// <summary>提交动作里「提交」那一支的回执值。</summary>
    private const string CommitChoice = "commit";

    /// <summary>
    /// 点「提交」之后到底要执行哪一条。
    ///
    /// 先把这次会带走的差异摆出来（<c>janus.proj.diff</c>，只读），连同两个出路一起
    /// 放进同一个弹窗：提交，或者丢弃这份脏工作树。在此之前人是看不见自己要提交什么的——
    /// 提交描述框在那儿，内容却得自己去别处翻。
    ///
    /// 返回 <c>Result</c> 表示这一轮到此为止（取消、无变更、已丢弃）；
    /// 返回 <c>Command</c> 表示接着执行它。
    /// </summary>
    private static async Task<(CommandResult? Result, string? Command)> DecideCommitAsync(
        CommandContext context, CommandBus bus, ProjectService projects, string name)
    {
        var diff = await projects.ReadWorktreeDiffAsync(
            name, RepositoryTarget.Parent, context.Cancellation);
        if (!diff.IsDirty)
            return (CommandResult.Ok($"{name}: {diff.Summary}"), null);

        var options = JsonSerializer.Serialize(new[]
        {
            new { label = "提交到本地仓库", value = CommitChoice },
            new { label = "删除本次脏工作树（不可撤销）", value = DiscardChoice },
        });
        // content 弹窗带 options 需要 Aurora ≥ 1.24.0：正文放差异，下面放两个出路。
        var chosen = await bus.ExecuteAsync(
            $"aurora.ui.dialog kind=content title={Quote($"提交 {name}")} " +
            $"body={Quote(diff.Summary)} content={Quote(diff.Body)} " +
            $"options={Quote(options)} primary=执行 cancel=取消 defaultcancel=true",
            context.Source, context.Cancellation);
        // 取消不是失败：人看完差异决定这次先不提交，是这个弹窗的正常出路之一。
        if (!chosen.Success)
            return (CommandResult.Ok($"{name}: 已取消，工作树未改动"), null);

        if (DialogValue(chosen) == DiscardChoice)
        {
            // 丢弃自带宿主确认（janus.proj.discard 是 Ask 级）；这里不再叠一层。
            var discarded = await bus.ExecuteAsync(
                $"janus.proj.discard name={Quote(name)}", context.Source, context.Cancellation);
            if (discarded.Success)
            {
                await RefreshCachedProjectAsync(projects, name, null, context.Cancellation);
                _ = await bus.ExecuteAsync(
                    "aurora.ui.refreshdata node=projects", context.Source, context.Cancellation);
            }
            return (discarded, null);
        }

        var message = context.GetString("msg")?.Trim() ?? "";
        if (message.Length == 0)
        {
            var prompt = await bus.ExecuteAsync(
                $"aurora.ui.dialog kind=prompt title=提交 body={Quote(name)} primary=提交 cancel=取消",
                context.Source, context.Cancellation);
            if (!prompt.Success) return (prompt, null);
            message = DialogValue(prompt);
        }
        return message.Length == 0
            ? (CommandResult.Fail("提交描述不能为空"), null)
            : (null, $"janus.proj.commit name={Quote(name)} msg={Quote(message)}");
    }

    /// <param name="snapshot">
    /// 已经 fetch 过的状态；给了就直接用。断网时若再按不 fetch 重算，
    /// 陈旧的远端引用会把「刷新」算成「同步」或「推送」。
    /// </param>
    internal static async Task RefreshCachedProjectAsync(
        ProjectService projects, string name, ProjectLifecycleSnapshot? snapshot,
        CancellationToken cancellation = default)
    {
        await ProjectCacheGate.WaitAsync(cancellation);
        try
        {
            var cached = _projectCache ?? [];
            var previous = cached.FirstOrDefault(item => item.BranchName.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? new WorktreeInfo(name, Path.Combine(projects.LibraryRoot, name));
            // 推送/同步命令已更新远端引用；只读当前项目，不再次 fetch 全库。
            var updated = snapshot != null
                ? ProjectService.ApplyLifecycle(previous, snapshot)
                : (await projects.ReadLifecycleStatusesAsync([previous], false, cancellation))[0];
            if (!updated.IsArchived)
            {
                var log = await GitRunner.RunAsync(updated.WorktreePath, ["log", "-1", "--format=%cI%x1f%s"], cancellation);
                if (log.Success)
                {
                    var parts = log.Output.Trim().Split('\u001f', 2);
                    updated = updated with { LastCommitTime = parts[0], LastCommitMessage = parts.Length > 1 ? parts[1] : "" };
                }
            }
            _projectCache = cached.Any(item => ReferenceEquals(item, previous))
                ? cached.Select(item => ReferenceEquals(item, previous) ? updated : item).ToArray()
                : [.. cached, updated];
        }
        finally { ProjectCacheGate.Release(); }
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
