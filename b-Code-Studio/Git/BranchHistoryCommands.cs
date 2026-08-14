using HistoryVulcan.Core.Commands;

namespace HistoryJanus.Git;

/// <summary>分支历史、恢复提交、本地硬重置与 lease 强推指令。</summary>
public static class BranchHistoryCommands
{
    public static void RegisterAll(
        CommandRegistry registry,
        BranchHistoryService service,
        HistoryRecorder history,
        string source = "app")
    {
        registry.Register(BuildHistory(service), source);
        registry.Register(BuildShow(service), source);
        registry.Register(BuildDiff(service), source);
        registry.Register(BuildRollback(service, history), source);
        registry.Register(BuildReset(service, history), source);
        registry.Register(BuildForcePush(service, history), source);
    }

    private static CommandDescriptor BuildHistory(BranchHistoryService service) => new()
    {
        Name = "janus.history.list",
        CommandClass = "history",
        Summary = "查看分支从父分支分叉点到当前 HEAD 的提交历史",
        Readonly = true,
        Example = "janus.history.list name=2026-018-MyAPI limit=200 remote=false",
        Parameters =
        [
            Text("name", "已登记项目名（目录名）", required: true, position: 0),
            Int("limit", "本次最多显示的自有提交数（1~2000）", "200"),
            Int("skip", "从最新提交向前跳过的数量", "0"),
            Bool("refresh", "重新扫描继承树后计算父分支", "false"),
            Bool("remote", "先刷新所选 origin 分支的跟踪引用", "false"),
        ],
        Handler = async ctx =>
        {
            var result = await service.GetHistoryAsync(ctx.RequireString("name"),
                ctx.GetInt("limit", 200), ctx.GetInt("skip"), ctx.GetBool("refresh"),
                ctx.GetBool("remote"), ctx.Progress, ctx.Cancellation);
            return result.Success
                ? CommandResult.Ok(result.Message, result.Report)
                : CommandResult.Fail(result.Message);
        },
    };

    private static CommandDescriptor BuildShow(BranchHistoryService service) => new()
    {
        Name = "janus.history.show",
        CommandClass = "history",
        Summary = "查看分支历史节点的提交详情与文件变更",
        Readonly = true,
        Example = "janus.history.show name=2026-018-MyAPI sha=abc1234",
        Parameters = TargetParameters(),
        Handler = async ctx =>
        {
            var result = await service.GetCommitAsync(ctx.RequireString("name"),
                ctx.RequireString("sha"), ctx.Cancellation);
            return result.Success
                ? CommandResult.Ok(result.Message, result.Detail)
                : CommandResult.Fail(result.Message);
        },
    };

    private static CommandDescriptor BuildDiff(BranchHistoryService service) => new()
    {
        Name = "janus.history.diff",
        CommandClass = "history",
        Summary = "预览历史节点与当前分支 HEAD 的提交及文件差异",
        Readonly = true,
        Example = "janus.history.diff name=2026-018-MyAPI sha=abc1234",
        Parameters = TargetParameters(),
        Handler = async ctx =>
        {
            var result = await service.GetDiffAsync(ctx.RequireString("name"),
                ctx.RequireString("sha"), ctx.Cancellation);
            return result.Success
                ? CommandResult.Ok(result.Message, result.Report)
                : CommandResult.Fail(result.Message);
        },
    };

    private static CommandDescriptor BuildRollback(
        BranchHistoryService service, HistoryRecorder history) => new()
        {
            Name = "janus.history.rollback",
            CommandClass = "history",
            Summary = "把工作树恢复到历史节点内容并生成新的恢复提交",
            Example = "janus.history.rollback name=2026-018-MyAPI sha=abc1234 msg=\"恢复到稳定版本\"",
            Parameters =
        [
            Text("name", "已登记项目名（目录名）", required: true, position: 0),
            Text("sha", "分叉点至 HEAD 范围内的提交 SHA", required: true, position: 1),
            Text("msg", "新恢复提交的说明", required: true, position: 2),
        ],
            ConfirmPrompt = ctx => service.BuildRollbackPrompt(
                ctx.RequireString("name"), ctx.RequireString("sha"), hardReset: false),
            Handler = async ctx =>
            {
                var expectedHead = service.TakeRollbackApproval(
                    ctx.RequireString("name"), ctx.RequireString("sha"), hardReset: false);
                var report = await service.RollbackAsync(ctx.RequireString("name"),
                    ctx.RequireString("sha"), ctx.RequireString("msg"), ctx.Cancellation, expectedHead);
                Record(history, "rollback", report);
                return report.Success
                    ? CommandResult.Ok(report.Message, report)
                    : CommandResult.Fail(report.Message);
            },
        };

    private static CommandDescriptor BuildReset(
        BranchHistoryService service, HistoryRecorder history) => new()
        {
            Name = "janus.history.reset",
            CommandClass = "history",
            Summary = "把非保护分支硬重置到历史节点（仅本地，不修改远端）",
            Example = "janus.history.reset name=2026-018-MyAPI sha=abc1234",
            Parameters = TargetParameters(),
            ConfirmPrompt = ctx => service.BuildRollbackPrompt(
                ctx.RequireString("name"), ctx.RequireString("sha"), hardReset: true),
            Handler = async ctx =>
            {
                var expectedHead = service.TakeRollbackApproval(
                    ctx.RequireString("name"), ctx.RequireString("sha"), hardReset: true);
                var report = await service.ResetAsync(ctx.RequireString("name"),
                    ctx.RequireString("sha"), ctx.Cancellation, expectedHead);
                Record(history, "reset", report);
                return report.Success
                    ? CommandResult.Ok(report.Message, report)
                    : CommandResult.Fail(report.Message);
            },
        };

    private static CommandDescriptor BuildForcePush(
        BranchHistoryService service, HistoryRecorder history) => new()
        {
            Name = "janus.history.forcepush",
            CommandClass = "history",
            Summary = "使用 --force-with-lease 更新非保护远端分支",
            Example = "janus.history.forcepush name=2026-018-MyAPI",
            Parameters = [Text("name", "已登记项目名（目录名）", required: true, position: 0)],
            ConfirmPrompt = ctx => service.BuildForcePushPrompt(ctx.RequireString("name")),
            Handler = async ctx =>
            {
                var expected = service.TakeForcePushApproval(ctx.RequireString("name"));
                var report = await service.ForcePushAsync(ctx.RequireString("name"), ctx.Cancellation,
                    expected.Local, expected.Remote);
                Record(history, "forcepush", report);
                return report.Success
                    ? CommandResult.Ok(report.Message, report)
                    : CommandResult.Fail(report.Message);
            },
        };

    private static IReadOnlyList<ParameterSpec> TargetParameters() =>
    [
        Text("name", "已登记项目名（目录名）", required: true, position: 0),
        Text("sha", "分叉点至 HEAD 范围内的提交 SHA", required: true, position: 1),
    ];

    private static ParameterSpec Text(
        string name, string description, bool required = false, int? position = null) => new()
        {
            Name = name,
            Description = description,
            Required = required,
            Position = position,
        };

    private static ParameterSpec Int(string name, string description, string defaultValue) => new()
    {
        Name = name,
        Description = description,
        Type = ParamType.Int,
        Default = defaultValue,
    };

    private static ParameterSpec Bool(string name, string description, string defaultValue) => new()
    {
        Name = name,
        Description = description,
        Type = ParamType.Bool,
        Default = defaultValue,
    };

    private static void Record(HistoryRecorder history, string action, BranchMutationReport report)
    {
        var message = $"target={report.TargetSha ?? "(无)"}; " +
                      $"before={report.BeforeSha ?? "(无)"}; after={report.AfterSha ?? "(无)"}; " +
                      report.Message;
        history.Record(report.Branch, action, message,
            report.Success ? report.Changed ? "成功" : "跳过" : "失败");
    }
}
