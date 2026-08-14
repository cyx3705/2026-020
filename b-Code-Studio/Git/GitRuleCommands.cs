using System.Text;
using System.Text.Json;
using HistoryVulcan.Core.Commands;

namespace HistoryJanus.Git;

public static class GitRuleCommands
{
    public static void RegisterAll(
        CommandRegistry registry,
        GitFileRuleService service,
        FormatInventoryService inventory,
        ProjectService projects,
        string source = "app")
    {
        registry.Register(BuildList(service), source);
        registry.Register(BuildSet(service), source);
        registry.Register(BuildBatchSet(service), source);
        registry.Register(BuildRemove(service), source);
        registry.Register(BuildScan(inventory), source);
        registry.Register(BuildReview(inventory, projects), source);
        registry.Register(BuildSync(service, projects), source);
    }

    private static CommandDescriptor BuildSync(GitFileRuleService service, ProjectService projects) => new()
    {
        Name = "janus.gitrule.sync",
        CommandClass = "gitrule",
        Summary = "把模板项目的规则基线刷入各项目的 baseline 块(不动项目自身 managed 块与手写内容)",
        Example = "janus.gitrule.sync apply=false",
        Parameters =
        [
            StringParam("name", "目标项目;省略则同步全部项目(模板自身除外)", position: 0),
            BoolParam("apply", "false 仅预览;true 经确认后写入", "false"),
        ],
        ConfirmPrompt = ctx => ctx.GetBool("apply")
            ? $"确认把 {projects.BaseBranch} 的规则基线写入 " +
              $"{(string.IsNullOrWhiteSpace(ctx.GetString("name")) ? "全部项目" : ctx.GetString("name"))} 的 baseline 块？\n\n" +
              "只重刷 baseline 块;项目自身 managed 块与托管块外手写内容不受影响。\n" +
              "不会删除本地文件、不提交、不推送、不重写历史。"
            : null,
        Handler = async ctx =>
        {
            var (success, message) = await service.SyncBaselineAsync(
                ctx.GetString("name"), ctx.GetBool("apply"), ctx.Progress, ctx.Cancellation);
            return success ? CommandResult.Ok(message) : CommandResult.Fail(message);
        },
    };

    // ---------------------------------------------------------------- 全覆盖扫描

    private static CommandDescriptor BuildScan(FormatInventoryService inventory) => new()
    {
        Name = "janus.gitrule.scan",
        CommandClass = "gitrule",
        Summary = "扫描项目库全部文件格式,输出台账与覆盖率(省略 name 扫全库)",
        Readonly = true,
        Example = "janus.gitrule.scan depth=normal",
        Parameters =
        [
            StringParam("name", "项目名;省略则扫描全库", position: 0),
            BoolParam("deep", "true 时追加 git lfs 指针核验(较慢)", "false"),
            BoolParam("refresh", "true 时忽略缓存全量重扫", "false"),
        ],
        Handler = async ctx =>
        {
            var (success, message, report) = await inventory.ScanAsync(
                ctx.GetString("name"), ctx.GetBool("deep"),
                ctx.GetBool("refresh"), ctx.Progress, ctx.Cancellation);
            return success ? CommandResult.Ok(message, report) : CommandResult.Fail(message);
        },
    };

    private static CommandDescriptor BuildReview(
        FormatInventoryService inventory, ProjectService projects) => new()
        {
            Name = "janus.gitrule.review",
            CommandClass = "gitrule",
            Summary = "一次扫描合并查看未决格式、目录候选、规则建议与需人工判断的未知格式",
            Readonly = true,
            Example = "janus.gitrule.review",
            Parameters = [StringParam("name", "项目名;省略则针对全库", position: 0)],
            Handler = async ctx =>
            {
                var (success, message, report) = await inventory.ReviewAsync(
                    // 用警告阈值(proj.warnmb,默认 50MB)而非拒绝阈值:
                    // 超过警告线的二进制就该走 LFS,不必等到触发硬拒绝
                    ctx.GetString("name"), projects.WarnBytes, ctx.Progress, ctx.Cancellation);
                return success ? CommandResult.Ok(message, report) : CommandResult.Fail(message);
            },
        };

    private static CommandDescriptor BuildList(GitFileRuleService service) => new()
    {
        Name = "janus.gitrule.list",
        CommandClass = "gitrule",
        Summary = "列出项目根文件格式的纳入 Git、LFS、LF 规则和实际索引状态",
        Readonly = true,
        Example = "janus.gitrule.list name=0000-000-Template",
        Parameters = [ProjectName()],
        Handler = async ctx =>
        {
            var result = await service.ListAsync(ctx.RequireString("name"), ctx.Cancellation);
            if (!result.Success)
                return CommandResult.Fail(result.Message);
            var text = new StringBuilder(result.Message);
            foreach (var rule in result.Rules)
            {
                text.Append($"\n  {rule.Pattern,-16} Git={(rule.Track ? "是" : "否")}" +
                            $" LFS={(rule.Lfs ? "是" : "否")} LF={(rule.Lf ? "是" : "否")}" +
                            $" 文件={rule.FileCount}  {rule.Status}");
            }
            return CommandResult.Ok(text.ToString(), result.Rules);
        },
    };

    private static CommandDescriptor BuildSet(GitFileRuleService service) => new()
    {
        Name = "janus.gitrule.set",
        CommandClass = "gitrule",
        Summary = "预览或确认后设置文件格式的纳入 Git、LFS、LF 状态并同步索引",
        Example = "janus.gitrule.set name=demo pattern=*.xlsx track=true lfs=true lf=false apply=false",
        Parameters =
        [
            ProjectName(),
            StringParam("pattern", "简单文件格式，如 *.xlsx", required: true, position: 1),
            BoolParam("track", "是否纳入 Git", "true"),
            BoolParam("lfs", "是否使用 LFS 指针", "false"),
            BoolParam("lf", "是否作为文本并统一 LF", "false"),
            BoolParam("apply", "false 仅预览；true 经本地确认后写入并同步索引", "false"),
        ],
        ConfirmPrompt = ctx => ctx.GetBool("apply")
            ? $"确认设置 {ctx.GetString("name")} 的 {ctx.GetString("pattern")} 文件规则并同步 Git 索引？不会删除本地文件、提交、推送或重写历史。"
            : null,
        Handler = async ctx => Render(await service.SetAsync(
            ctx.RequireString("name"), ctx.RequireString("pattern"),
            ctx.GetBool("track", true), ctx.GetBool("lfs"), ctx.GetBool("lf"),
            ctx.GetBool("apply"), ctx.Cancellation)),
    };

    private static CommandDescriptor BuildBatchSet(GitFileRuleService service) => new()
    {
        Name = "janus.gitrule.batchset",
        CommandClass = "gitrule",
        Summary = "一次预览、确认并保存多条 Git/LFS/LF 文件规则",
        Example = "janus.gitrule.batchset name=demo changes=\"[{\\\"pattern\\\":\\\"*.xlsx\\\",\\\"track\\\":true,\\\"lfs\\\":true,\\\"lf\\\":false}]\" apply=false",
        Parameters =
        [
            ProjectName(),
            StringParam("changes", "1 至 500 条规则的 JSON 数组", required: true, position: 1),
            BoolParam("apply", "false 仅预览；true 经一次确认后批量写入并同步索引", "false"),
        ],
        ConfirmPrompt = ctx => ctx.GetBool("apply")
            ? $"确认批量设置 {ctx.GetString("name")} 的 {BatchCount(ctx.GetString("changes"))} 条文件规则并同步 Git 索引？" +
              "不会删除本地文件、提交、推送或重写历史。"
            : null,
        Handler = async ctx =>
        {
            IReadOnlyList<GitFileRuleChange> changes;
            try
            {
                changes = JsonSerializer.Deserialize<List<GitFileRuleChange>>(
                              ctx.RequireString("changes"),
                              new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                          ?? [];
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail($"changes 不是有效的规则 JSON 数组: {ex.Message}");
            }
            var result = await service.BatchSetAsync(
                ctx.RequireString("name"), changes, ctx.GetBool("apply"), ctx.Cancellation);
            return result.Success
                ? CommandResult.Ok(result.Message, result.Preview)
                : CommandResult.Fail(result.Message);
        },
    };

    private static CommandDescriptor BuildRemove(GitFileRuleService service) => new()
    {
        Name = "janus.gitrule.remove",
        CommandClass = "gitrule",
        Summary = "预览或确认后移除托管文件格式规则；不删除本地文件",
        Example = "janus.gitrule.remove name=demo pattern=*.xlsx apply=false",
        Parameters =
        [
            ProjectName(),
            StringParam("pattern", "要移除的简单文件格式", required: true, position: 1),
            BoolParam("apply", "false 仅预览；true 经本地确认后移除", "false"),
        ],
        ConfirmPrompt = ctx => ctx.GetBool("apply")
            ? $"确认移除 {ctx.GetString("name")} 的 {ctx.GetString("pattern")} 托管规则？不会删除本地文件。"
            : null,
        Handler = async ctx => Render(await service.RemoveAsync(
            ctx.RequireString("name"), ctx.RequireString("pattern"),
            ctx.GetBool("apply"), ctx.Cancellation)),
    };

    private static CommandResult Render(
        (bool Success, string Message, GitFileRulePreview? Preview) result)
        => result.Success
            ? CommandResult.Ok(result.Message, result.Preview)
            : CommandResult.Fail(result.Message);

    private static ParameterSpec ProjectName() => new()
    {
        Name = "name",
        Description = "已登记项目名（目录名）",
        Required = true,
        Position = 0,
    };

    private static int BatchCount(string? json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<GitFileRuleChange>>(
                json ?? "[]", new JsonSerializerOptions { PropertyNameCaseInsensitive = true })?.Count ?? 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static ParameterSpec StringParam(
        string name, string description, bool required = false, int? position = null) => new()
        {
            Name = name,
            Description = description,
            Required = required,
            Position = position,
        };

    private static ParameterSpec BoolParam(string name, string description, string defaultValue) => new()
    {
        Name = name,
        Description = description,
        Type = ParamType.Bool,
        Default = defaultValue,
    };
}
