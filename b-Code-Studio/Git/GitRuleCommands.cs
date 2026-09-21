using HistoryVulcan.Core.Commands;

namespace HistoryJanus.Git;

/// <summary>
/// gitrule.* 指令域，两块规则各管各的（5.11.0 拆开）：
/// 入库规则——全库共用的「不纳入仓库」清单的读与改，没有下发命令，由提交链路自动刷进各仓托管块；
/// LFS 规则——按项目：看实况（lfs）、给 ≥100MB 的文件定去向（lfsset）、把不合规的仓修回来（lfsrepair）。
/// 不到 100MB 的文件一律不走 LFS。
/// </summary>
public static class GitRuleCommands
{
    public static void RegisterAll(
        CommandRegistry registry,
        GitFileRuleService service,
        LfsRuleService lfs,
        string source = "app")
    {
        registry.Register(BuildList(service), source);
        registry.Register(BuildLfs(lfs), source);
        registry.Register(BuildLfsSet(lfs), source);
        registry.Register(BuildLfsRepair(lfs), source);
        registry.Register(BuildExcludes(service), source);
    }

    private static CommandDescriptor BuildLfs(LfsRuleService service) => new()
    {
        Name = "janus.gitrule.lfs",
        CommandClass = "gitrule",
        Summary = "列出该项目仓的 LFS 指针文件与 ≥100MB 文件，带各自的决定与违规汇总",
        Readonly = true,
        Example = "janus.gitrule.lfs name=2026-018-MyAPI",
        Parameters =
        [
            new ParameterSpec
            {
                Name = "name",
                Description = "已登记项目名（目录名）",
                Required = true,
                Position = 0,
            },
        ],
        Handler = async ctx =>
        {
            var (success, message, report) = await service.InspectAsync(
                ctx.RequireString("name"), ctx.Cancellation);
            return success ? CommandResult.Ok(message, report) : CommandResult.Fail(message);
        },
    };

    private static CommandDescriptor BuildLfsSet(LfsRuleService service) => new()
    {
        Name = "janus.gitrule.lfsset",
        CommandClass = "gitrule",
        Summary = "给一个 ≥100MB 的文件定去向：LFS 指针或不再纳入 git，下次提交生效，历史不变",
        Example = "janus.gitrule.lfsset name=2026-018-MyAPI path=b-Data/big.bin decision=lfs",
        Parameters =
        [
            new ParameterSpec { Name = "name", Description = "已登记项目名（目录名）", Required = true, Position = 0 },
            new ParameterSpec { Name = "path", Description = "仓库内相对路径，正斜杠分隔", Required = true, Position = 1 },
            new ParameterSpec
            {
                Name = "decision",
                Description = "lfs = 走 LFS 指针；ignore = 不再纳入 git（本地文件保留）；none = 清除决定",
                Required = true,
                Position = 2,
            },
        ],
        Handler = async ctx =>
        {
            var decision = ctx.RequireString("decision").Trim().ToLowerInvariant() switch
            {
                "lfs" => LfsDecision.Lfs,
                "ignore" => LfsDecision.Ignore,
                "none" => (LfsDecision?)LfsDecision.None,
                _ => null,
            };
            if (decision == null)
                return CommandResult.Fail("decision 只接受 lfs / ignore / none");
            var (success, message) = await service.SetDecisionAsync(
                ctx.RequireString("name"), ctx.RequireString("path"), decision.Value, ctx.Cancellation);
            return success ? CommandResult.Ok(message) : CommandResult.Fail(message);
        },
    };

    private static CommandDescriptor BuildLfsRepair(LfsRuleService service) => new()
    {
        Name = "janus.gitrule.lfsrepair",
        CommandClass = "gitrule",
        Summary = "把一个仓修回 LFS 规则：不足 100MB 的指针转回普通入库，≥100MB 的按精确路径保留",
        Example = "janus.gitrule.lfsrepair name=2026-018-MyAPI dryRun=true",
        Parameters =
        [
            new ParameterSpec { Name = "name", Description = "已登记项目名（目录名）", Required = true, Position = 0 },
            new ParameterSpec
            {
                Name = "commit",
                Description = "是否本地提交（按 300MB 分批，每批一个提交）；不推送",
                Type = ParamType.Bool,
                Default = "true",
            },
            new ParameterSpec
            {
                Name = "dryRun",
                Description = "只报告计划，不改文件、不动索引",
                Type = ParamType.Bool,
                Default = "false",
            },
        ],
        // 会改 .gitattributes 并把几百个文件重新入库，必须确认；预演不需要。
        Level = CommandLevel.Ask,
        ConfirmPrompt = ctx =>
            $"确认修复项目 {ctx.GetString("name")} 的 LFS 规则？\n\n" +
            "• 去掉 lfs 托管块之外的所有 filter=lfs（保留 -text）\n" +
            "• 不足 100MB 的 LFS 指针转回普通入库；本机缺实体的先 git lfs pull，取不到的暂留指针\n" +
            "• ≥100MB 的按精确路径继续走 LFS\n" +
            (ctx.GetBool("commit", true) ? "• 本地提交，按 300MB 分批\n" : "• 只暂存，不提交\n") +
            "不改写历史，不推送。",
        Handler = async ctx =>
        {
            var (success, message, report) = await service.RepairAsync(
                ctx.RequireString("name"), ctx.GetBool("commit", true), ctx.GetBool("dryRun"),
                ctx.Progress, ctx.Cancellation);
            return success ? CommandResult.Ok(message, report) : CommandResult.Fail(message);
        },
    };

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
        Level = CommandLevel.Ask,
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
