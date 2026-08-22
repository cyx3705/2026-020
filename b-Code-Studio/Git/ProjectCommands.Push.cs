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
        Description = "缺 origin 时自动新建的 GitHub 仓库可见性；已有 origin 时无效",
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
        Summary = "推送单个分支；可先推直属子模块，全部成功后再推父项目",
        Example = "janus.proj.push name=2026-018-MyAPI target=both",
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
                Description = "兼容参数：true=both，false=parent；target 存在时忽略",
                Type = ParamType.Bool,
                Default = "false",
            },
            VisibilityParameter(),
        ],
        Handler = async ctx =>
        {
            var name = ctx.RequireString("name");
            var report = await projects.PushAsync(name, ResolveTarget(ctx),
                ctx.GetString("visibility"), ctx.Cancellation);
            history.Record(name, "push",
                $"target={report.Target}; 子模块={report.Submodules?.Count ?? 0}; " +
                $"parentPushed={report.ParentPushed}; pointerPending={report.ParentPointerPending}" +
                (report.RemoteCreated
                    ? $"; 新建远端={report.RemoteUrl}（{report.RemoteVisibility}）"
                    : string.Empty),
                report.Success ? "成功" : "失败");
            RecordSubmodules(history, name, "submodule.push", report.Submodules ?? []);
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
        Summary = "推送全部分支；可先去重推送所有直属子模块",
        Example = "janus.proj.pushall target=both",
        Parameters =
        [
            TargetParameter(),
            new ParameterSpec
            {
                Name = "submodules",
                Description = "兼容参数：true=both，false=parent；target 存在时忽略",
                Type = ParamType.Bool,
                Default = "false",
            },
            VisibilityParameter(),
        ],
        Level = CommandLevel.Ask,
        ConfirmPrompt = ctx =>
            "确定要向各项目仓的 origin 推送当前 HEAD 吗?\n\n失败的仓会隔离报告，不会中止其余仓。" +
            $"\n未配置 origin 的项目会按 visibility={ResolveVisibility(ctx)} 在 GitHub 上新建同名仓库。" +
            (ResolveTarget(ctx) switch
            {
                RepositoryTarget.Submodules => "\n本次只推送全部直属子模块，父分支不会推送。",
                RepositoryTarget.Both => "\n直属子模块将先推送，任一失败都会阻止父仓库推送。",
                _ => string.Empty,
            }),
        Handler = async ctx =>
        {
            var report = await projects.PushAllAsync(ResolveTarget(ctx),
                ctx.GetString("visibility"), ctx.Cancellation);
            var newRemotes = (report.CreatedRemotes ?? [])
                .Where(remote => remote.Created).ToList();
            history.Record("(全部)", "pushall",
                $"子模块={report.Submodules.Count}" +
                (newRemotes.Count > 0
                    ? $"; 新建远端={string.Join(", ", newRemotes.Select(remote => $"{remote.FullName}（{remote.Visibility}）"))}"
                    : string.Empty),
                report.Success ? "成功" : "失败");
            RecordSubmodules(history, "(全部)", "submodule.push", report.Submodules);
            return report.Success
                ? CommandResult.Ok(report.Message, report)
                : Fail(report.Message, report);
        },
    };
}
