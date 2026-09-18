using HistoryVulcan.Core.Commands;

namespace HistoryJanus.Git;

public static partial class ProjectCommands
{
    private static CommandDescriptor BuildRefresh(ProjectService projects) => new()
    {
        Name = "janus.proj.refresh",
        CommandClass = "proj",
        Summary = "查询各项目远端并刷新生命周期状态",
        Example = "janus.proj.refresh",
        Handler = async ctx =>
        {
            var rows = await projects.RefreshProjectsAsync(ctx.Cancellation);
            return CommandResult.Ok($"已刷新 {rows.Count} 个项目的远端状态", rows);
        },
    };

    private static CommandDescriptor BuildSync(ProjectService projects, HistoryRecorder history) => new()
    {
        Name = "janus.proj.sync",
        CommandClass = "proj",
        Summary = "fetch 后仅快进同步项目，并记录两端已验证 SHA",
        Example = "janus.proj.sync name=2026-018-MyAPI",
        Parameters = [ProjectNameParameter()],
        Handler = async ctx =>
        {
            var name = ctx.RequireString("name");
            var result = await projects.SyncAsync(name, ctx.Cancellation);
            history.Record(name, "sync", result.Snapshot?.Message ?? result.Message,
                result.Success ? "成功" : "失败");
            return result.Success
                ? CommandResult.Ok(result.Message, result.Snapshot)
                : CommandResult.Fail(result.Message);
        },
    };

    private static CommandDescriptor BuildArchive(ProjectService projects, HistoryRecorder history) => new()
    {
        Name = "janus.proj.archive",
        CommandClass = "proj",
        Summary = "复核远端一致后只保留直属 z/Z 文件夹并移除本地工作树",
        Example = "janus.proj.archive name=2026-018-MyAPI",
        Parameters = [ProjectNameParameter()],
        Level = CommandLevel.Ask,
        ConfirmPrompt = ctx =>
        {
            var name = ctx.RequireString("name").Trim();
            return projects.IsProtected(name) ? null
                : $"确定归档项目 {name} 吗？\n\n将重新 fetch 并验证本地/远端一致，随后只保留项目根下直属 z/Z 文件夹。";
        },
        Handler = async ctx =>
        {
            var name = ctx.RequireString("name");
            var result = await projects.ArchiveAsync(name, ctx.Cancellation);
            history.Record(name, "archive", result.Message, result.Success ? "成功" : "失败");
            return result.Success ? CommandResult.Ok(result.Message) : CommandResult.Fail(result.Message);
        },
    };

    private static CommandDescriptor BuildPull(ProjectService projects, HistoryRecorder history) => new()
    {
        Name = "janus.proj.pull",
        CommandClass = "proj",
        Summary = "从归档记录分段取回远端并优先恢复本地 z/Z 内容（逐段报进度，可取消）",
        Example = "janus.proj.pull name=2026-018-MyAPI",
        Parameters = [ProjectNameParameter()],
        Handler = async ctx =>
        {
            var name = ctx.RequireString("name");
            var result = await projects.PullAsync(name, ctx.Progress, ctx.Cancellation);
            history.Record(name, "pull", result.Message, result.Success ? "成功" : "失败");
            return result.Success ? CommandResult.Ok(result.Message) : CommandResult.Fail(result.Message);
        },
    };

    private static ParameterSpec ProjectNameParameter() => new()
    {
        Name = "name",
        Description = "已登记项目名（目录名）",
        Required = true,
        Position = 0,
    };
}
