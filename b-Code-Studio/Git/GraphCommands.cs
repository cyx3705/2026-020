using HistoryVulcan.Core.Commands;

namespace HistoryJanus.Git;

/// <summary>只读 DAG 图谱指令；无 ConfirmPrompt，不改写仓库。</summary>
public static class GraphCommands
{
    public static void RegisterAll(
        CommandRegistry registry,
        GraphService service,
        string source = "app")
    {
        registry.Register(BuildSummary(service), source);
        registry.Register(BuildBranches(service), source);
        registry.Register(BuildCommits(service), source);
        registry.Register(BuildNode(service), source);
    }

    private static CommandDescriptor BuildSummary(GraphService service) => new()
    {
        Name = "janus.graph.summary",
        CommandClass = "graph",
        Summary = "查看编号项目图谱摘要：HEAD、节点数、分支与工作树 dirty 状态",
        Readonly = true,
        Example = "janus.graph.summary name=2026-020-HistoryJanus limit=200",
        Parameters =
        [
            Text("name", "已登记项目名（目录名）", required: true, position: 0),
            Int("limit", "本次计入摘要的提交上限（1~2000）", "200"),
        ],
        Handler = async ctx =>
        {
            var result = await service.GetSummaryAsync(
                ctx.RequireString("name"), ctx.GetInt("limit", 200), ctx.Cancellation);
            return result.Success
                ? CommandResult.Ok(result.Message, result.Summary)
                : CommandResult.Fail(result.Message);
        },
    };

    private static CommandDescriptor BuildBranches(GraphService service) => new()
    {
        Name = "janus.graph.branches",
        CommandClass = "graph",
        Summary = "列出编号项目的主线与平行分支（含已合并历史）及基线关系",
        Readonly = true,
        Example = "janus.graph.branches name=2026-020-HistoryJanus",
        Parameters =
        [
            Text("name", "已登记项目名（目录名）", required: true, position: 0),
        ],
        Handler = async ctx =>
        {
            var result = await service.GetBranchesAsync(ctx.RequireString("name"), ctx.Cancellation);
            return result.Success
                ? CommandResult.Ok(result.Message, result.Report)
                : CommandResult.Fail(result.Message);
        },
    };

    private static CommandDescriptor BuildCommits(GraphService service) => new()
    {
        Name = "janus.graph.commits",
        CommandClass = "graph",
        Summary = "按时间序分页读取项目窗口内的提交节点与父边（含 merge parent）",
        Readonly = true,
        Example = "janus.graph.commits name=2026-020-HistoryJanus limit=200 skip=0",
        Parameters =
        [
            Text("name", "已登记项目名（目录名）", required: true, position: 0),
            Int("limit", "本次最多返回的提交数（1~2000）", "200"),
            Int("skip", "从最新提交向前跳过的数量", "0"),
        ],
        Handler = async ctx =>
        {
            var result = await service.GetCommitsAsync(
                ctx.RequireString("name"), ctx.GetInt("limit", 200),
                ctx.GetInt("skip"), ctx.Cancellation);
            return result.Success
                ? CommandResult.Ok(result.Message, result.Report)
                : CommandResult.Fail(result.Message);
        },
    };

    private static CommandDescriptor BuildNode(GraphService service) => new()
    {
        Name = "janus.graph.node",
        CommandClass = "graph",
        Summary = "读取单个提交的父边、文件差异摘要；证据槽本阶段为空",
        Readonly = true,
        Example = "janus.graph.node name=2026-020-HistoryJanus sha=abc1234",
        Parameters =
        [
            Text("name", "已登记项目名（目录名）", required: true, position: 0),
            Text("sha", "提交 SHA（完整或缩写）", required: true, position: 1),
        ],
        Handler = async ctx =>
        {
            var result = await service.GetNodeAsync(
                ctx.RequireString("name"), ctx.RequireString("sha"), ctx.Cancellation);
            return result.Success
                ? CommandResult.Ok(result.Message, result.Detail)
                : CommandResult.Fail(result.Message);
        },
    };

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
}
