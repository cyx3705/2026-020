using HistoryVulcan.Core.Commands;

namespace HistoryJanus.Git;

/// <summary>
/// gitrule.* 指令域：只暴露「全库共用的不纳入仓库清单」的读与改。
/// 没有下发命令——清单由提交链路每次自动刷进各仓托管块。
/// 也没有 LFS 相关命令——LFS 只在提交链路对超 100MB 的具体文件征求同意。
/// </summary>
public static class GitRuleCommands
{
    public static void RegisterAll(
        CommandRegistry registry,
        GitFileRuleService service,
        string source = "app")
    {
        registry.Register(BuildList(service), source);
        registry.Register(BuildExcludes(service), source);
    }

    private static CommandDescriptor BuildList(GitFileRuleService service) => new()
    {
        Name = "janus.gitrule.list",
        CommandClass = "gitrule",
        Summary = "查看全库共用的不纳入仓库清单，以及各项目托管块是否已落地",
        Readonly = true,
        Example = "janus.gitrule.list",
        Parameters =
        [
            new ParameterSpec
            {
                Name = "name",
                Description = "只看某个项目；省略则列出全库",
                Position = 0,
            },
        ],
        Handler = async ctx =>
        {
            var (success, message, report) = await service.ListAsync(
                ctx.GetString("name"), ctx.Cancellation);
            return success ? CommandResult.Ok(message, report) : CommandResult.Fail(message);
        },
    };

    private static CommandDescriptor BuildExcludes(GitFileRuleService service) => new()
    {
        Name = "janus.gitrule.excludes",
        CommandClass = "gitrule",
        Summary = "改写全库共用的不纳入仓库清单（目录以 / 结尾，后缀写 *.xxx）",
        Example = "janus.gitrule.excludes list=\"bin/, obj/, venv/, *.user, *.log\"",
        Parameters =
        [
            new ParameterSpec
            {
                Name = "list",
                Description = "逗号或空格分隔的完整清单；本命令整体替换而非追加",
                Required = true,
                Position = 0,
            },
        ],
        // 清单是全库共用的，一次改动影响每一个项目的下次提交，因此必须确认。
        ConfirmPrompt = ctx =>
            "确认替换**全库共用**的不纳入仓库清单？\n\n" +
            $"新清单: {ctx.GetString("list")}\n\n" +
            "该清单在各项目下次提交时自动刷进 .gitignore 托管块。\n" +
            "本命令只改设置：不删除本地文件、不动索引、不提交、不推送。\n" +
            "已被跟踪却命中新排除项的文件不会自动移出索引，janus.gitrule.list 会点名它们。",
        Handler = ctx =>
        {
            var (success, message) = service.SetExcludeList(ctx.RequireString("list"));
            return Task.FromResult(success
                ? CommandResult.Ok(message)
                : CommandResult.Fail(message));
        },
    };
}
