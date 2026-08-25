using System.IO;
using System.Text.Json;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Modules;
using HistoryVulcan.Core.Storage;
using HistoryJanus.Git;

namespace HistoryJanus.Module;

/// <summary>
/// HistoryVulcan 5.0+ 的模块入口。Janus 只登记业务命令和可序列化的页面协议，
/// 不再实现宿主 UI 生命周期，也不把 WPF 对象带过宿主边界。
/// </summary>
public sealed class HistoryJanusUiModule : IModuleContextAware
{
    private StudioBusinessComposition? _business;

    public void Attach(IModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_business != null)
            throw new InvalidOperationException("HistoryJanus module context is already attached.");

        context.RegisterCommands(registry =>
        {
            var settings = new ModuleSettings();
            var log = new ModuleLog();
            var dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "HistoryVulcan", "Modules", "HistoryJanus", "data");

            _business = StudioBusinessCompositionFactory.Register(
                registry,
                context.Bus,
                settings,
                log,
                dataDirectory,
                "module:HistoryJanus");

            HistoryJanusUiCommands.Register(registry, context.Bus, "module:HistoryJanus");
        });
    }
}

internal static class HistoryJanusUiCommands
{
    private const string Domain = "janus";
    private const string Owner = "HistoryJanus";

    public static void Register(CommandRegistry registry, CommandBus bus, string source)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "janus.ui.describe",
            Domain = Domain,
            CommandClass = "ui",
            Summary = "返回 Janus 页面描述（页面注册协议 V1）",
            Readonly = true,
            HiddenReason = "界面内部协议，对模型无意义",
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(DescriptionJson)),
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "janus.ui.actions",
            Domain = Domain,
            CommandClass = "ui",
            Summary = "返回 Janus 页面动作声明（动作协议 V1）",
            Readonly = true,
            HiddenReason = "界面内部协议，对模型无意义",
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(ActionsJson)),
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "janus.ui.data",
            Domain = Domain,
            CommandClass = "ui",
            Summary = "按页面视图返回 Janus 页面数据",
            Readonly = true,
            HiddenReason = "界面内部协议，对模型无意义",
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "view",
                    Description = "页面视图：projects、graph、history、rules、github",
                    Required = true,
                    Position = 0,
                },
            ],
            Handler = context => LoadDataAsync(context, bus),
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "janus.ui.graphnode",
            Domain = Domain,
            CommandClass = "ui",
            Summary = "返回图谱提交节点详情",
            Readonly = true,
            HiddenReason = "界面内部协议，对模型无意义",
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "node",
                    Description = "页面图谱节点 ID",
                    Required = true,
                    Position = 0,
                },
            ],
            Handler = context => LoadGraphNodeAsync(context, bus),
        }, source);
    }

    private static async Task<CommandResult> LoadDataAsync(CommandContext context, CommandBus bus)
    {
        var view = context.GetString("view")?.Trim().ToLowerInvariant();
        if (view == "graph")
            return await LoadGraphAsync(context, bus);

        var command = view switch
        {
            // 页面初次建树发生在宿主启动路径上；工作树状态由用户刷新时再取，
            // 避免首屏同步触发 45 个仓库的 Git 状态扫描。
            "projects" => "janus.proj.list status=false",
            "history" => "janus.history.list",
            "rules" => "janus.gitrule.list",
            "github" => "janus.github.status",
            _ => null,
        };

        if (command == null)
            return CommandResult.Fail($"未知 Janus 页面视图: {view}");

        var result = await bus.ExecuteAsync(command, context.Source, context.Cancellation);
        if (!result.Success)
            return CommandResult.Fail(result.Message);

        // Aurora 进程外取数时 Data 不保证跨边界保留，Message 必须携带同一份 JSON。
        var payload = JsonSerializer.Serialize(result.Data);
        return CommandResult.Ok(payload, payload);
    }

    private static async Task<CommandResult> LoadGraphAsync(CommandContext context, CommandBus bus)
    {
        var projects = await bus.ExecuteAsync("janus.proj.list status=false", context.Source, context.Cancellation);
        if (!projects.Success || projects.Data == null)
            return CommandResult.Fail(projects.Message);

        var projectJson = JsonSerializer.Serialize(projects.Data);
        using var projectDocument = JsonDocument.Parse(projectJson);
        var firstProject = projectDocument.RootElement.EnumerateArray().FirstOrDefault();
        var name = firstProject.ValueKind == JsonValueKind.Object
            && firstProject.TryGetProperty("name", out var nameProperty)
            ? nameProperty.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(name))
            return CommandResult.Ok("暂无可显示项目", new { schemaVersion = 1, title = "暂无项目", lanes = Array.Empty<object>(), nodes = Array.Empty<object>() });

        var result = await bus.ExecuteAsync(
            $"janus.graph.commits name={Quote(name)}", context.Source, context.Cancellation);
        if (!result.Success || result.Data is not GraphCommitsReport report)
            return CommandResult.Fail(result.Message);

        var nodeIds = report.Nodes.ToDictionary(
            node => node.Sha,
            node => name + "|" + node.Sha,
            StringComparer.OrdinalIgnoreCase);
        var lanes = report.Lanes.Select((lane, index) => new
        {
            id = "lane-" + index,
            title = lane.Name,
            tip = nodeIds.GetValueOrDefault(lane.TargetSha, name + "|" + lane.TargetSha),
            open = lane.IsOpen,
        });
        var nodes = report.Nodes.Select(node => new
        {
            id = nodeIds[node.Sha],
            title = string.IsNullOrWhiteSpace(node.ShortSha) ? node.Sha[..Math.Min(8, node.Sha.Length)] : node.ShortSha,
            subtitle = node.Subject,
            parents = node.Parents.Where(nodeIds.ContainsKey).Select(parent => nodeIds[parent]).ToArray(),
        });
        var description = new
        {
            schemaVersion = 1,
            title = $"{name} · {report.Nodes.Count} 个节点",
            lanes,
            nodes,
            selectAction = "janus.graph.node.detail",
        };
        return CommandResult.Ok(JsonSerializer.Serialize(description), description);
    }

    private static async Task<CommandResult> LoadGraphNodeAsync(CommandContext context, CommandBus bus)
    {
        var node = context.GetString("node")?.Trim();
        var separator = node?.IndexOf('|', StringComparison.Ordinal) ?? -1;
        if (separator <= 0 || separator == node!.Length - 1)
            return CommandResult.Fail("图谱节点 ID 无效");

        var name = node[..separator];
        var sha = node[(separator + 1)..];
        return await bus.ExecuteAsync(
            $"janus.graph.node name={Quote(name)} sha={Quote(sha)}",
            context.Source,
            context.Cancellation);
    }

    private static string Quote(string value)
        => value.Contains(' ') || value.Contains('"')
            ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : value;

    private static readonly string DescriptionJson = JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        owner = Owner,
        pages = new object[]
        {
            new
            {
                id = "overview",
                title = "项目总览",
                placement = new { side = "center", visible = true, singleton = true },
                content = new
                {
                    type = "stack",
                    gap = "normal",
                    children = new object[]
                    {
                        new { type = "text", text = "项目与工作树" },
                        new
                        {
                            type = "table",
                            id = "projects",
                            dataSource = new { command = "janus.ui.data", args = new { view = "projects" } },
                            columns = new object[]
                            {
                                new { key = "name", title = "项目", width = "220" },
                                new { key = "isClean", title = "状态", width = "90" },
                                new { key = "subject", title = "最近提交", width = "*" },
                            },
                            view = new { filterable = true, sortable = true, selection = "single" },
                        },
                    },
                },
            },
            new
            {
                id = "graph",
                title = "分支图谱",
                placement = new { side = "tab", tabTarget = "console", visible = true, singleton = true },
                content = new
                {
                    type = "swimlane",
                    dataSource = new { command = "janus.ui.data", args = new { view = "graph" } },
                },
            },
            new
            {
                id = "projops",
                title = "项目操作",
                placement = new { side = "left", ratio = 0.38, visible = true, singleton = true },
                content = new
                {
                    type = "stack",
                    gap = "normal",
                    children = new object[]
                    {
                        new { type = "text", text = "提交、推送与项目治理" },
                        new { type = "input", id = "commit-message", text = "提交描述", suggest = "commands" },
                        new { type = "button", text = "刷新规则", invoke = new { action = "janus.rules.refresh" } },
                        new { type = "button", text = "刷新 GitHub", invoke = new { action = "janus.github.refresh" } },
                    },
                },
            },
        },
    }, new JsonSerializerOptions { WriteIndented = false });

    private static readonly string ActionsJson = JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        owner = Owner,
        actions = new object[]
        {
            new { id = "janus.rules.refresh", title = "刷新规则", command = "janus.gitrule.list", summary = "读取全库排除规则" },
            new { id = "janus.github.refresh", title = "刷新 GitHub", command = "janus.github.status", summary = "读取当前项目 GitHub 状态" },
            new { id = "janus.graph.node.detail", title = "查看提交", command = "janus.ui.graphnode", args = new { node = "{node}" }, summary = "查看图谱节点详情" },
        },
    }, new JsonSerializerOptions { WriteIndented = false });
}

internal sealed class ModuleSettings : ISettingsService
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public string? Get(string key) => _values.GetValueOrDefault(key);
    public int GetInt(string key, int fallback) => int.TryParse(Get(key), out var value) ? value : fallback;
    public void Set(string key, string value) => _values[key] = value;
    public IReadOnlyList<KeyValuePair<string, string>> All() => _values.ToList();
}

internal sealed class ModuleLog : IShellLog
{
    public event EventHandler<ShellLogEntry>? EntryAdded;
    public IReadOnlyList<ShellLogEntry> Snapshot() => [];

    public void Log(ShellLogLevel level, string category, string message)
        => EntryAdded?.Invoke(this, new ShellLogEntry(DateTime.Now, level, category, message));
}
