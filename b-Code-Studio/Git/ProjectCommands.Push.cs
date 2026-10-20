using HistoryVulcan.Core.Commands;
using HistoryJanus.GitHub;

namespace HistoryJanus.Git;

/// <summary>
/// proj.* 的推送切面。推送与提交共享 target 语义，但多了一条提交链路没有的分支：
/// 仓库还没有 origin 时按需在 GitHub 上建远端，因此单独成文件。
/// </summary>
public static partial class ProjectCommands
{
    private static ParameterSpec VisibilityParameter() => new()
    {
        Name = "visibility",
        Description = "首次推送时不存在的 GitHub 仓库可见性；已有 origin 或远端仓库时保持原值",
        AllowedValues = ["public", "private"],
        Default = GitHubRepositoryProvisioner.DefaultVisibility,
    };

    private static string ResolveVisibility(CommandContext context)
        => string.IsNullOrWhiteSpace(context.GetString("visibility"))
            ? GitHubRepositoryProvisioner.DefaultVisibility
            : context.GetString("visibility")!.Trim().ToLowerInvariant();

    // ---------------------------------------------------------------- janus.proj.push

    private static CommandDescriptor BuildPush(ProjectService projects, HistoryRecorder history) => new()
    {
        Name = "janus.proj.push",
        CommandClass = "proj",
        Summary = "推送单个项目；被拒时交 AI 检查分叉，无冲突则自动合并后重推",
        Example = "janus.proj.push name=2026-018-MyAPI",
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
                Description = "保留的兼容参数。项目只有一份仓库，这个值不再另选范围",
                Type = ParamType.Bool,
                Default = "false",
            },
            VisibilityParameter(),
        ],
        Handler = async ctx =>
        {
            var name = ctx.RequireString("name");
            var report = await projects.PushAsync(name, ResolveTarget(ctx),
                ctx.GetString("visibility"), ctx.Cancellation, ctx.Progress);
            if (report.Success)
                projects.InvalidateVerification(name);
            history.Record(name, "push",
                $"parentPushed={report.ParentPushed}" +
                (report.RemoteCreated
                    ? $"; 新建远端={report.RemoteUrl}（{report.RemoteVisibility}）"
                    : string.Empty),
                report.Success ? "成功" : "失败");
            return report.Success
                ? CommandResult.Ok(report.Message, report)
                : Fail(report.Message, report);
        },
    };

    // ---------------------------------------------------------------- janus.proj.pushall

    private static CommandDescriptor BuildPushAll(ProjectService projects, HistoryRecorder history) => new()
    {
        Name = "janus.proj.pushall",
        CommandClass = "proj",
        Summary = "推送全部项目；被拒的仓交 AI 检查分叉，无冲突则自动合并后重推",
        Example = "janus.proj.pushall",
        Parameters =
        [
            TargetParameter(),
            new ParameterSpec
            {
                Name = "submodules",
                Description = "保留的兼容参数。项目只有一份仓库，这个值不再另选范围",
                Type = ParamType.Bool,
                Default = "false",
            },
            VisibilityParameter(),
        ],
        Level = CommandLevel.Ask,
        ConfirmPrompt = ctx =>
            "确定要向各项目仓的 origin 推送当前 HEAD 吗?\n\n失败的仓会隔离报告，不会中止其余仓。" +
            $"\n未配置 origin 的项目会按 visibility={ResolveVisibility(ctx)} 在 GitHub 上新建同名仓库。",
        Handler = async ctx =>
        {
            var report = await projects.PushAllAsync(ResolveTarget(ctx),
                ctx.GetString("visibility"), ctx.Cancellation, ctx.Progress);
            var newRemotes = (report.CreatedRemotes ?? [])
                .Where(remote => remote.Created).ToList();
            history.Record("(全部)", "pushall",
                newRemotes.Count > 0
                    ? $"新建远端={string.Join(", ", newRemotes.Select(remote => $"{remote.FullName}（{remote.Visibility}）"))}"
                    : "全部项目",
                report.Success ? "成功" : "失败");
            return report.Success
                ? CommandResult.Ok(report.Message, report)
                : Fail(report.Message, report);
        },
    };
}
