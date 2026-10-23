using HistoryJanus.Git;
using HistoryVulcan.Core.Commands;

namespace HistoryJanus.Module;

/// <summary>
/// 总览项目表的右键菜单（5.17.0，用户定）：以此新建项目、固定到桌面、以现在的分支新建分支、合并当前分支。
/// </summary>
/// <remarks>
/// 「固定到桌面」不经这里：动作直接跨域调 <c>mercury.proj.pin</c>（Mercury 的桌面扩展坞），
/// Janus 不认识坞，也不该替它判断满没满。其余三条都要先弹窗问一句，所以收在一条界面指令里。
/// 三条都只看被右键的那一行（<c>{name}</c>），不看总览里选中的是哪一行。
/// </remarks>
internal static partial class HistoryJanusUiCommands
{
    private static void RegisterProjectMenuCommands(
        ICommandRegistrar registry, ICommandBus bus, Func<StudioBusinessComposition?> business)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "janus.ui.projectop",
            Domain = Domain,
            CommandClass = "ui",
            Summary = "总览项目表右键菜单：以此新建项目、以现在的分支新建分支、合并当前分支",
            Example = "janus.ui.projectop 2026-020-HistoryJanus merge",
            HiddenReason = "本机界面动作（要弹窗）；模型请用 janus.proj.create / janus.branch.*",
            Parameters =
            [
                new ParameterSpec { Name = "name", Description = "被右键那一行的项目工作树名称，例如 2026-020-HistoryJanus", Required = true, Position = 0 },
                new ParameterSpec
                {
                    Name = "op",
                    Description = "create 以此为模板新建项目、branch 从当前分支分出新分支、merge 把当前分支合并进 main",
                    Required = true,
                    Position = 1,
                    AllowedValues = [ProjectOpCreate, ProjectOpBranch, ProjectOpMerge],
                },
            ],
            Handler = context => ProjectMenuAsync(context, bus, business),
        });
    }

    private static async Task<CommandResult> ProjectMenuAsync(
        CommandContext context, ICommandBus bus, Func<StudioBusinessComposition?> business)
    {
        var name = context.RequireString("name").Trim();
        var op = context.RequireString("op").Trim().ToLowerInvariant();
        if (business() is not { } composition)
            return CommandResult.Fail("Janus 项目服务尚未就绪");

        switch (op)
        {
            case ProjectOpCreate:
            {
                var prompt = await bus.ExecuteAsync(
                    $"aurora.ui.dialog kind=prompt title=以此新建项目 body={Quote($"以 {name} 为模板新建的项目名")} primary=新建 cancel=取消",
                    context.Source, context.Cancellation);
                var created = DialogValue(prompt).Trim();
                if (!prompt.Success || created.Length == 0)
                    return CommandResult.Ok($"{name}：已取消新建");
                // 新建本身（含进度与历史记录）归 janus.proj.create；总览要看见新项目得重取。
                var result = await bus.ExecuteAsync(
                    $"janus.proj.create name={Quote(created)} base={Quote(name)}", context.Source, context.Cancellation);
                _ = await bus.ExecuteAsync("aurora.ui.refreshdata node=projects", context.Source);
                return result;
            }
            case ProjectOpBranch:
            case ProjectOpMerge:
                break;
            default:
                return CommandResult.Fail($"未知项目操作：{op}");
        }

        var branches = composition.Branches;
        var current = await branches.CurrentBranchAsync(name, context.Cancellation);
        if (current.Length == 0)
            return CommandResult.Fail($"{name}：读不到当前分支（未初始化、已归档或 HEAD 游离）");

        try
        {
            if (op == ProjectOpBranch)
            {
                var prompt = await bus.ExecuteAsync(
                    $"aurora.ui.dialog kind=prompt title=新建分支 body={Quote($"从当前分支 {current} 分出的新分支名")} primary=新建 cancel=取消",
                    context.Source, context.Cancellation);
                if (!prompt.Success)
                    return CommandResult.Ok($"{name}：已取消新建分支");
                return ToCommandResult(await branches.CreateAsync(name, current, DialogValue(prompt), context.Cancellation));
            }

            // 合并当前分支 = 把它并进 main（用户口中的「收分支」）。合并要在 main 的工作树上做，
            // 所以先切到 main 再合；完成后工作树停在 main。有冲突时 MergeAsync 整个退回，人仍在 main 上。
            var main = ProjectService.MainlineBranch;
            if (current.Equals(main, StringComparison.Ordinal))
                return CommandResult.Ok($"{name}：当前就在 {main} 上，没有要合并的分支");
            var confirm = await bus.ExecuteAsync(
                $"aurora.ui.dialog kind=confirm title=合并当前分支 body={Quote($"把 {current} 合并进 {main}？\n完成后工作树停在 {main}；有冲突时合并整个退回。")} primary=合并 cancel=取消",
                context.Source, context.Cancellation);
            if (!confirm.Success)
                return CommandResult.Ok($"{name}：已取消合并");
            var switched = await RunOnCleanWorktreeAsync(context, bus, composition, name,
                () => branches.SwitchAsync(name, main, context.Cancellation));
            if (!switched.Success)
                return ToCommandResult(switched);
            return ToCommandResult(await branches.MergeAsync(name, current, context.Cancellation));
        }
        finally
        {
            // 分支与 HEAD 动过：项目操作页的历史表与总览那一行都要对上现场。
            _ = await bus.ExecuteAsync("aurora.ui.refreshdata node=history-rows", context.Source);
            await RefreshCachedProjectAsync(composition.Projects, name, null);
            _ = await bus.ExecuteAsync("aurora.ui.refreshdata node=projects", context.Source);
        }
    }
}
