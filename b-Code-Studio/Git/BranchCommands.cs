using HistoryVulcan.Core.Commands;

namespace HistoryJanus.Git;

/// <summary>项目仓内分支的列举、分出、切换、合并与删除（5.16.0）。</summary>
public static class BranchCommands
{
    public static void RegisterAll(ICommandRegistrar registry, BranchService service)
    {
        registry.Register(BuildList(service));
        registry.Register(BuildCreate(service));
        registry.Register(BuildSwitch(service));
        registry.Register(BuildMerge(service));
        registry.Register(BuildDelete(service));
    }

    private static CommandDescriptor BuildList(BranchService service) => new()
    {
        Name = "janus.branch.list",
        CommandClass = "branch",
        Summary = "列出项目仓的本地分支：当前分支、最近提交与上游差距",
        Readonly = true,
        Example = "janus.branch.list name=2026-020-HistoryJanus",
        Parameters = [Text("name", "已登记项目名（目录名）", required: true, position: 0)],
        Handler = async ctx =>
        {
            var result = await service.ListAsync(ctx.RequireString("name"), ctx.Cancellation);
            return result.Success
                ? CommandResult.Ok(result.Message, result.Report)
                : CommandResult.Fail(result.Message);
        },
    };

    private static CommandDescriptor BuildCreate(BranchService service) => new()
    {
        Name = "janus.branch.create",
        CommandClass = "branch",
        Summary = "从指定分支分出一条新的本地分支，不切换工作树",
        Example = "janus.branch.create name=2026-020-HistoryJanus from=main branch=feature-x",
        Parameters =
        [
            Text("name", "已登记项目名（目录名）", required: true, position: 0),
            Text("from", "起点分支名，例如 main", required: true, position: 1),
            Text("branch", "新分支名，须符合 git 分支命名规则，例如 feature-x", required: true, position: 2),
        ],
        Handler = async ctx => ToResult(await service.CreateAsync(
            ctx.RequireString("name"), ctx.RequireString("from"), ctx.RequireString("branch"), ctx.Cancellation)),
    };

    private static CommandDescriptor BuildSwitch(BranchService service) => new()
    {
        Name = "janus.branch.switch",
        CommandClass = "branch",
        Summary = "把项目工作树切换到另一条本地分支；工作树有未提交内容时拒绝",
        Example = "janus.branch.switch name=2026-020-HistoryJanus branch=feature-x",
        Parameters =
        [
            Text("name", "已登记项目名（目录名）", required: true, position: 0),
            Text("branch", "要切换到的本地分支名，例如 feature-x", required: true, position: 1),
        ],
        Handler = async ctx => ToResult(await service.SwitchAsync(
            ctx.RequireString("name"), ctx.RequireString("branch"), ctx.Cancellation)),
    };

    private static CommandDescriptor BuildMerge(BranchService service) => new()
    {
        Name = "janus.branch.merge",
        CommandClass = "branch",
        Summary = "把指定分支合并到当前分支；工作树不干净或有冲突时什么都不动",
        Example = "janus.branch.merge name=2026-020-HistoryJanus branch=feature-x",
        Parameters =
        [
            Text("name", "已登记项目名（目录名）", required: true, position: 0),
            Text("branch", "要合并进来的本地分支名，例如 feature-x", required: true, position: 1),
        ],
        Handler = async ctx => ToResult(await service.MergeAsync(
            ctx.RequireString("name"), ctx.RequireString("branch"), ctx.Cancellation)),
    };

    private static CommandDescriptor BuildDelete(BranchService service) => new()
    {
        Name = "janus.branch.delete",
        CommandClass = "branch",
        Summary = "删除一条本地分支；不能删当前分支与 main，未合并的要 force=true",
        Example = "janus.branch.delete name=2026-020-HistoryJanus branch=feature-x",
        Parameters =
        [
            Text("name", "已登记项目名（目录名）", required: true, position: 0),
            Text("branch", "要删除的本地分支名，例如 feature-x", required: true, position: 1),
            new ParameterSpec
            {
                Name = "force",
                Description = "未合并到当前分支也删除（丢弃它独有的提交）",
                Type = ParamType.Bool,
                Default = "false",
            },
        ],
        Level = CommandLevel.Ask,
        ConfirmPrompt = ctx =>
        {
            var branch = ctx.RequireString("branch").Trim();
            return ctx.GetBool("force")
                ? $"确定要强制删除分支 {branch} 吗?\n\n它尚未合并到当前分支，独有的提交会一起丢失。"
                : $"确定要删除分支 {branch} 吗?";
        },
        Handler = async ctx => ToResult(await service.DeleteAsync(
            ctx.RequireString("name"), ctx.RequireString("branch"), ctx.GetBool("force"), ctx.Cancellation)),
    };

    /// <summary>失败时 Data 带上结果，界面据 <see cref="BranchMutationResult.Dirty"/> 决定是否弹提交窗口。</summary>
    private static CommandResult ToResult(BranchMutationResult result)
        => result.Success
            ? CommandResult.Ok(result.Message, result)
            : CommandResult.Fail(result.Message);

    private static ParameterSpec Text(
        string name, string description, bool required = false, int? position = null) => new()
        {
            Name = name,
            Description = description,
            Required = required,
            Position = position,
        };
}
