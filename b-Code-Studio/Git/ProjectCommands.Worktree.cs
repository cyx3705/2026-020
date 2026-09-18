using HistoryVulcan.Core.Commands;

namespace HistoryJanus.Git;

/// <summary>
/// proj.* 里关于脏工作树的两条：提交前看差异，以及不提交、全丢掉。
/// 它们是同一个决定的两条出路，所以成对登记、共用同一套范围参数。
/// </summary>
public static partial class ProjectCommands
{
    private static CommandDescriptor BuildDiff(ProjectService projects) => new()
    {
        Name = "janus.proj.diff",
        CommandClass = "proj",
        Summary = "读取项目脏工作树相对 HEAD 的差异（只读，不碰索引）",
        Readonly = true,
        Example = "janus.proj.diff name=2026-018-MyAPI",
        Parameters =
        [
            new ParameterSpec
            {
                Name = "name",
                Description = "已登记项目名（目录名）",
                Required = true,
                Position = 0,
            },
            TargetParameter(),
            new ParameterSpec
            {
                Name = "submodules",
                Description = "兼容参数；存在 target 时以 target 为准",
                Type = ParamType.Bool,
                Default = "false",
            },
        ],
        Handler = async ctx =>
        {
            var report = await projects.ReadWorktreeDiffAsync(
                ctx.RequireString("name"), ResolveTarget(ctx), ctx.Cancellation);
            return CommandResult.Ok(report.Summary, report);
        },
    };

    private static CommandDescriptor BuildDiscard(ProjectService projects, HistoryRecorder history) => new()
    {
        Name = "janus.proj.discard",
        CommandClass = "proj",
        Summary = "丢弃项目脏工作树：回到 HEAD 并删除未跟踪文件（不删被忽略的文件）",
        Example = "janus.proj.discard name=2026-018-MyAPI",
        Parameters =
        [
            new ParameterSpec
            {
                Name = "name",
                Description = "已登记项目名（目录名）",
                Required = true,
                Position = 0,
            },
            TargetParameter(),
            new ParameterSpec
            {
                Name = "submodules",
                Description = "兼容参数；存在 target 时以 target 为准",
                Type = ParamType.Bool,
                Default = "false",
            },
        ],
        // 不可撤销：未跟踪文件直接删除，不进回收站，也没有可回滚的提交。
        Level = CommandLevel.Ask,
        ConfirmPrompt = ctx =>
        {
            var name = ctx.RequireString("name").Trim();
            return projects.IsProtected(name)
                ? null
                : $"确定丢弃项目 {name} 的脏工作树吗？\n\n" +
                  "将执行 git reset --hard HEAD 并删除未跟踪的新文件。\n" +
                  "**这一步不可撤销**：未跟踪文件不进回收站，已跟踪的改动也没有可回滚的提交。\n" +
                  "被 .gitignore 排除的内容（bin/obj、venv、node_modules 等）不动。";
        },
        Handler = async ctx =>
        {
            var name = ctx.RequireString("name");
            if (projects.IsProtected(name.Trim()))
                return CommandResult.Fail($"\"{name.Trim()}\" 是受保护项目，拒绝丢弃工作树");
            var report = await projects.DiscardAsync(
                name, ResolveTarget(ctx), ctx.Progress, ctx.Cancellation);
            history.Record(name, "discard", report.Message, report.Success ? "成功" : "失败",
                report.Repositories.Count);
            return report.Success
                ? CommandResult.Ok(report.Message, report)
                : Fail(report.Message, report);
        },
    };
}
