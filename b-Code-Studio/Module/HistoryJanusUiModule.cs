using System.IO;
using System.Text.Json;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Modules;
using HistoryVulcan.Core.Storage;
using HistoryJanus.Git;
using HistoryJanus.GitHub;

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

            HistoryJanusUiCommands.Register(
                registry, context.Bus, "module:HistoryJanus", () => _business);
        });
    }
}

internal static class HistoryJanusUiCommands
{
    private const string Domain = "janus";
    private const string Owner = "HistoryJanus";

    /// <summary>
    /// 项目选择通道（Aurora 1.8.16 起）。「项目总览」页的表格往这里发布选中行，
    /// 「项目操作」页的控制面板按它取值——两页各渲染一次，页内节点 id 跨不过去，
    /// 通道名跨得过去。名字以自己的域起头；同名通道只认第一个声明方。
    /// </summary>
    private const string ProjectChannel = "janus.project";

    /// <summary>动作参数里取「当前选中项目」的写法。只写一次，改通道名不必满文件找。</summary>
    private const string SelectedProject = "{selection." + ProjectChannel + ".name}";

    /// <summary>
    /// 子页面通道（Aurora 1.8.17 起）。「项目操作」页控制面板里的轮换选项框往这里发布
    /// 当前标题，同一页的 <c>switch</c> 容器按它决定下面显示哪一批控件。
    ///
    /// 5.4.5 里这三块是三个 <c>side=bottom</c> 的页面，由停靠层并成底部标签组。
    /// 标签组是**三页**，各占一条底边，各自还顶着一段说明文字；收进一页之后，
    /// 版面只剩一条选项框，而三块内容拿到的是同一块完整高度。
    /// </summary>
    private const string SectionChannel = "janus.section";

    /// <summary>三个子页面的标题。它们同时是选项框的候选项和 switch 的 case，只写一处。</summary>
    private const string SectionRules = "Git 文件规则";

    private const string SectionHistory = "分支历史";

    private const string SectionGitHub = "GitHub";

    public static void Register(
        CommandRegistry registry,
        CommandBus bus,
        string source,
        Func<StudioBusinessComposition?> business)
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
                    Description = "页面视图：projects、graph、history、excludes、rulestate、github",
                    Required = true,
                    Position = 0,
                },
                new ParameterSpec
                {
                    Name = "name",
                    Description = "项目名；history / rulestate / graph 需要，由页面按当前选中行填入",
                    Position = 1,
                },
            ],
            Handler = context => LoadDataAsync(context, bus, business),
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

        registry.Register(new CommandDescriptor
        {
            Name = "janus.ui.refreshrules",
            Domain = Domain,
            CommandClass = "ui",
            Summary = "重取排除清单与当前项目的落地状态",
            Readonly = true,
            HiddenReason = "界面内部协议，对模型无意义",
            Handler = context => RefreshRulesAsync(context, bus),
        }, source);
    }

    /// <summary>
    /// 「刷新规则」的落点。它要刷的是**两张表**，而 <c>aurora.ui.refreshdata</c>
    /// 一次只收一个 page 或一个 node。
    ///
    /// 5.4.5 之前这两张表独占一页，因此按 <c>page=rules</c> 一把刷正好。收进「项目操作」页
    /// 之后，同一页上还挂着分支历史与 GitHub 两块——按页刷会把它们一起带上，
    /// 而 GitHub 那条要探 SSH 与凭据助手。所以改成点名刷这两个节点。
    /// </summary>
    private static async Task<CommandResult> RefreshRulesAsync(CommandContext context, CommandBus bus)
    {
        var refreshed = 0;
        foreach (var node in new[] { "rule-list", "rule-state" })
        {
            var result = await bus.ExecuteAsync(
                "aurora.ui.refreshdata node=" + node,
                context.Source,
                context.Cancellation);

            // 一个节点失败不拦下另一个：两张表各自独立，刷到一张也比一张都不刷强。
            if (result.Success)
                refreshed++;
        }

        return refreshed > 0
            ? CommandResult.Ok($"已重取 {refreshed} 处规则数据")
            : CommandResult.Fail("规则表没有登记取数绑定，无处可刷");
    }

    private static async Task<CommandResult> LoadDataAsync(
        CommandContext context,
        CommandBus bus,
        Func<StudioBusinessComposition?> business)
    {
        var view = context.GetString("view")?.Trim().ToLowerInvariant();
        if (view == "graph")
            return await LoadGraphAsync(context, bus);

        // 清单本身存在设置里，读它不碰 Git。这一条必须与落地状态分开：
        // 落地状态要对每个项目跑两条 git ls-files，全库跑一遍是 90 次进程启动。
        if (view == "excludes")
            return Rows(UiRuleProjection.Excludes(business()));

        var command = view switch
        {
            // 页面初次建树发生在宿主启动路径上；工作树状态由用户刷新时再取，
            // 避免首屏同步触发 45 个仓库的 Git 状态扫描。
            "projects" => "janus.proj.list status=false",
            // 这三条都按**单个项目**取，项目名由页面从选中通道填入。
            // 不带项目名就不取——全库扫描不该由"打开一个页签"触发。
            "history" => Scoped(context, "janus.history.list", " limit=200"),
            "rulestate" => Scoped(context, "janus.gitrule.list", ""),
            "github" => "janus.github.status",
            _ => null,
        };

        if (command == null)
            return CommandResult.Fail($"未知 Janus 页面视图: {view}（或缺少 name）");

        var result = await bus.ExecuteAsync(command, context.Source, context.Cancellation);
        if (!result.Success)
            return CommandResult.Fail(result.Message);

        // Aurora 的表格只吃「字符串到字符串」的行。业务记录直接序列化出来的是
        // 帕斯卡命名加布尔/数字/嵌套对象，那种载荷解析会整条失败，症状是一张空表。
        return view switch
        {
            "projects" => Payload(UiProjectProjection.Serialize(result.Data)),
            "history" => Rows(UiHistoryProjection.Read(result.Data)),
            "rulestate" => Rows(UiRuleProjection.States(result.Data)),
            "github" => Rows(UiGitHubProjection.Read(result.Data)),
            _ => Payload(JsonSerializer.Serialize(result.Data)),
        };
    }

    /// <summary>按项目取数：没给项目名就返回 null，调用方据此拒绝，而不是去扫全库。</summary>
    private static string? Scoped(CommandContext context, string command, string suffix)
    {
        var name = context.GetString("name")?.Trim();
        return string.IsNullOrWhiteSpace(name) ? null : $"{command} name={Quote(name)}{suffix}";
    }

    /// <summary>Aurora 进程外取数时 Data 不保证跨边界保留，Message 必须携带同一份 JSON。</summary>
    private static CommandResult Payload(string json) => CommandResult.Ok(json, json);

    private static CommandResult Rows(IReadOnlyList<IReadOnlyDictionary<string, string>> rows)
        => Payload(JsonSerializer.Serialize(rows));

    private static async Task<CommandResult> LoadGraphAsync(CommandContext context, CommandBus bus)
    {
        // 项目名由页面从选中通道填入。此前这里固定取项目清单的**第一条**，
        // 于是无论在总览里选了谁，图谱画的都是同一个项目——不报错，只是一直不对。
        var name = context.GetString("name")?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return CommandResult.Ok(
                "请先选中一个项目",
                new { schemaVersion = 1, title = "请先在项目总览里选中一个项目", lanes = Array.Empty<object>(), nodes = Array.Empty<object>() });

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

    /// <summary>
    /// 页面行的通用构造。**Aurora 的表格只吃「字符串到字符串」的行**——
    /// 业务记录直接 `JsonSerializer.Serialize` 出来的是帕斯卡命名加布尔/数字/嵌套对象，
    /// 那种载荷在表格侧解析会整条失败，症状是一张空表加一条 Warn，
    /// 而空表看上去和"这个项目确实没有数据"一模一样。
    /// </summary>
    private static Dictionary<string, string> Row(params (string Key, string Value)[] cells)
    {
        var row = new Dictionary<string, string>(cells.Length, StringComparer.Ordinal);
        foreach (var (key, value) in cells)
            row[key] = value ?? "";
        return row;
    }

    /// <summary>Git 文件规则：全库共用的清单，以及某个项目的落地状态。</summary>
    internal static class UiRuleProjection
    {
        /// <summary>清单只读设置，不碰 Git——它要在打开页签时立刻出来。</summary>
        public static IReadOnlyList<IReadOnlyDictionary<string, string>> Excludes(
            StudioBusinessComposition? business)
        {
            if (business is null)
                return [Row(("kind", "—"), ("rule", "业务组合尚未装配"))];

            var rules = business.GitRules;
            return rules.Directories
                .Select(directory => Row(("kind", "目录"), ("rule", directory)))
                .Concat(rules.Suffixes.Select(suffix => Row(("kind", "后缀"), ("rule", suffix))))
                .ToList();
        }

        /// <summary>落地状态按项目取；每个项目两条 git ls-files，因此绝不整库跑。</summary>
        public static IReadOnlyList<IReadOnlyDictionary<string, string>> States(object? data)
            => data is not ExcludeRuleReport report
                ? []
                : report.Projects
                    .Select(state => Row(
                        ("project", state.Project),
                        ("block", state.BlockCurrent ? "已是当前清单" : "落后，下次提交自动刷新"),
                        ("ignored", state.IgnoredFileCount.ToString()),
                        ("tracked", state.TrackedButExcludedCount.ToString()),
                        ("detail", state.Detail)))
                    .ToList();
    }

    /// <summary>分支历史：从分叉点到 HEAD 的自有提交。</summary>
    internal static class UiHistoryProjection
    {
        public static IReadOnlyList<IReadOnlyDictionary<string, string>> Read(object? data)
            => data is not BranchHistoryReport report
                ? []
                : report.Entries
                    .Select(entry => Row(
                        ("sha", entry.ShortSha),
                        ("time", entry.TimeDisplay),
                        ("author", entry.Author),
                        ("marker", entry.Marker),
                        ("remote", entry.RemoteDisplay),
                        ("subject", entry.Subject)))
                    .ToList();
    }

    /// <summary>
    /// GitHub 连接状态。它不是一个列表而是一个对象，因此摊成「项 / 值」两列——
    /// 表格是本协议里唯一的展示组件，把对象硬塞成一行会得到一张只有一行、
    /// 列名全是英文字段名的表。
    /// </summary>
    internal static class UiGitHubProjection
    {
        public static IReadOnlyList<IReadOnlyDictionary<string, string>> Read(object? data)
        {
            if (data is not GitHubAccountOverview overview)
                return [];

            return
            [
                Row(("item", "Git"), ("value", overview.GitVersion)),
                Row(("item", "凭据管理器"), ("value", overview.CredentialManagerVersion)),
                Row(("item", "gh CLI"), ("value", overview.GhCliAvailable ? "可用" : "不可用")),
                Row(("item", "提交身份"),
                    ("value", $"{overview.EffectiveIdentity.Name} <{overview.EffectiveIdentity.Email}>（{overview.EffectiveIdentity.Source}）")),
                Row(("item", "origin fetch"), ("value", overview.Origin.FetchUrl)),
                Row(("item", "origin push"), ("value", overview.Origin.PushUrl)),
                Row(("item", "origin 归属"),
                    ("value", $"{overview.Origin.Host} / {overview.Origin.Owner} / {overview.Origin.Repository}")),
                Row(("item", "传输方式"), ("value", overview.Origin.Transport.ToString())),
                Row(("item", "GCM 账号"),
                    ("value", overview.CredentialAccounts.Count == 0
                        ? "（无）"
                        : string.Join("、", overview.CredentialAccounts.Select(account => account.Account)))),
                Row(("item", "SSH 公钥"),
                    ("value", overview.Ssh.PublicKeys.Count == 0
                        ? "（无）"
                        : string.Join("、", overview.Ssh.PublicKeys.Select(key => key.FileName)))),
                Row(("item", "SSH 探测"), ("value", $"{overview.Ssh.ProbeState}：{overview.Ssh.ProbeMessage}")),
                Row(("item", "连接状态"),
                    ("value", $"{overview.Connection.State}（{overview.Connection.Transport}）")),
                Row(("item", "读取时间"),
                    ("value", overview.LastCheckedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))),
            ];
        }
    }

    internal sealed record UiProjectRow(string Name, string IsClean, string Subject);

    internal static class UiProjectProjection
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        public static string Serialize(object? data)
            => JsonSerializer.Serialize(Read(data), Options);

        public static IReadOnlyList<UiProjectRow> Read(object? data)
        {
            if (data == null)
                return [];

            var json = data switch
            {
                string text => text,
                JsonElement element => element.GetRawText(),
                JsonDocument document => document.RootElement.GetRawText(),
                _ => JsonSerializer.Serialize(data, Options),
            };

            try
            {
                var projects = JsonSerializer.Deserialize<List<WorktreeInfo>>(json, Options) ?? [];
                return projects.Select(Project).ToList();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("Janus 项目数据不是合法 WorktreeInfo 数组。", ex);
            }
        }

        private static UiProjectRow Project(WorktreeInfo project)
            => new(
                project.BranchName,
                project.IsClean switch
                {
                    true => "干净",
                    false => "有修改",
                    null => "未知",
                },
                project.LastCommitMessage);
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

    /// <summary>门禁用：页面描述与动作声明必须能被逐条核对，不能只在真机上看。</summary>
    internal static string Description => DescriptionJson;

    /// <summary>门禁用：同上。</summary>
    internal static string ActionDeclarations => ActionsJson;

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
                            // 选中行发上界面级通道，左侧「项目操作」页的控制面板按它取值。
                            // 页内节点 id 到不了对面那一页，通道名可以。
                            channel = ProjectChannel,
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
                    dataSource = new
                    {
                        command = "janus.ui.data",
                        args = new { view = "graph", name = SelectedProject },
                    },
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
                        new
                        {
                            type = "panel",
                            id = "janus-projops",
                            text = "项目操作",
                            // 四行，行是**声明出来的**（Aurora 协议 V3 / REQ-UI-060），
                            // 不再是 inline 的副产品。
                            //
                            // 前三行走可变宽度：标签与按钮各自停在自己的最窄宽度，
                            // 中间那个文本框吃掉全部余量。1.9.2 之前这里是一个三列共享的
                            // Grid，三行的标签列、控件列、按钮列互相对齐得像张表——
                            // 而这三行本来就没有对齐的理由，对齐的代价是最长的那个按钮
                            // 把另外两行的输入框一起挤窄。
                            //
                            // 三个文本框都**不写必填**：Aurora 的 required 已在 V3 退役，
                            // 理由与这里当年不敢用它是同一条——它是全局的，
                            // 一个空框会把面板上每个按钮一起锁死。
                            rows = new object[]
                            {
                                new
                                {
                                    widgets = new object[]
                                    {
                                        new
                                        {
                                            kind = "textbox",
                                            id = "project-name",
                                            label = "项目名",
                                            flex = true,
                                            // 跟着选中行走；人可以就地改成新名字，再点「改名」。
                                            follows = ProjectChannel + ".name",
                                        },
                                        new
                                        {
                                            kind = "button",
                                            action = "janus.project.rename",
                                            text = "改名",
                                            enabledWhen = new { selected = ProjectChannel },
                                        },
                                    },
                                },
                                new
                                {
                                    widgets = new object[]
                                    {
                                        new
                                        {
                                            kind = "textbox",
                                            id = "new-project",
                                            label = "新项目名",
                                            flex = true,
                                        },
                                        new
                                        {
                                            kind = "button",
                                            action = "janus.project.create",
                                            text = "新建",
                                            enabledWhen = new { selected = ProjectChannel },
                                        },
                                    },
                                },
                                new
                                {
                                    widgets = new object[]
                                    {
                                        new
                                        {
                                            kind = "textbox",
                                            id = "commit-message",
                                            label = "提交描述",
                                            flex = true,
                                        },
                                        new
                                        {
                                            kind = "button",
                                            action = "janus.project.commit",
                                            text = "提交当前项目",
                                            enabledWhen = new { selected = ProjectChannel },
                                        },
                                        new
                                        {
                                            kind = "button",
                                            action = "janus.project.push",
                                            text = "推送当前项目",
                                            enabledWhen = new { selected = ProjectChannel },
                                        },
                                    },
                                },
                                // 第四行：子页面切换，**均布**。放在最后一行是因为它管的是
                                // 自己下面那块——隔着三行项目操作去指挥下面的内容，
                                // 看的人得先建立这条联系。
                                //
                                // 它不写 enabledWhen：切页面与选没选中项目无关，
                                // 而按选中启停会让"没选项目时连看一眼 GitHub 状态都不行"。
                                new
                                {
                                    mode = "even",
                                    widgets = new object[]
                                    {
                                        new
                                        {
                                            kind = "textbox",
                                            id = "section",
                                            label = "子页面",
                                            mode = "select",
                                            channel = SectionChannel,
                                            options = new[] { SectionRules, SectionHistory, SectionGitHub },
                                        },
                                    },
                                },
                            },
                        },
                        // 三块子内容。case 与上面选项框的候选项是同一组常量，
                        // 因此改标题只改常量，两处不会各说各话。
                        //
                        // 没被切到过的那几支不取数：Aurora 只把当前这一支挂上可视树。
                        // 落地状态那一支每次跑两条 git ls-files，
                        // GitHub 那一支要探 SSH 与凭据助手——没人看的时候不该付这个钱。
                        new
                        {
                            type = "switch",
                            id = "janus-sections",
                            source = "{selection." + SectionChannel + ".value}",
                            children = new object[]
                            {
                                new
                                {
                                    type = "stack",
                                    @case = SectionRules,
                                    gap = "tight",
                                    children = new object[]
                                    {
                                        new
                                        {
                                            type = "panel",
                                            id = "janus-rules-ops",
                                            text = "规则",
                                            rows = new object[]
                                            {
                                                new
                                                {
                                                    mode = "even",
                                                    widgets = new object[]
                                                    {
                                                        new
                                                        {
                                                            kind = "button",
                                                            action = "janus.rules.refresh",
                                                            text = "刷新规则",
                                                        },
                                                    },
                                                },
                                            },
                                        },
                                        new
                                        {
                                            type = "grid",
                                            min = 320,
                                            gap = "normal",
                                            children = new object[]
                                            {
                                                new
                                                {
                                                    type = "table",
                                                    id = "rule-list",
                                                    dataSource = new
                                                    {
                                                        command = "janus.ui.data",
                                                        args = new { view = "excludes" },
                                                    },
                                                    columns = new object[]
                                                    {
                                                        new { key = "kind", title = "类型", width = "60" },
                                                        new { key = "rule", title = "规则", width = "*" },
                                                    },
                                                },
                                                new
                                                {
                                                    type = "table",
                                                    id = "rule-state",
                                                    // 落地状态要跑 git ls-files，因此只看**当前选中的那一个**项目。
                                                    // 全库跑一遍是 90 次进程启动，不该由"切到这一支"触发。
                                                    dataSource = new
                                                    {
                                                        command = "janus.ui.data",
                                                        args = new { view = "rulestate", name = SelectedProject },
                                                    },
                                                    columns = new object[]
                                                    {
                                                        new { key = "block", title = "托管块", width = "160" },
                                                        new { key = "ignored", title = "已忽略", width = "70" },
                                                        new { key = "tracked", title = "已跟踪却应排除", width = "110" },
                                                        new { key = "detail", title = "说明", width = "*" },
                                                    },
                                                },
                                            },
                                        },
                                    },
                                },
                                new
                                {
                                    type = "table",
                                    @case = SectionHistory,
                                    id = "history-rows",
                                    dataSource = new
                                    {
                                        command = "janus.ui.data",
                                        args = new { view = "history", name = SelectedProject },
                                    },
                                    columns = new object[]
                                    {
                                        new { key = "sha", title = "提交", width = "80" },
                                        new { key = "time", title = "时间", width = "150" },
                                        new { key = "author", title = "作者", width = "100" },
                                        new { key = "marker", title = "标记", width = "60" },
                                        new { key = "remote", title = "远端", width = "80" },
                                        new { key = "subject", title = "说明", width = "*" },
                                    },
                                    view = new { filterable = true, sortable = true, selection = "single" },
                                },
                                new
                                {
                                    type = "stack",
                                    @case = SectionGitHub,
                                    gap = "tight",
                                    children = new object[]
                                    {
                                        new
                                        {
                                            type = "panel",
                                            id = "janus-github-ops",
                                            text = "GitHub",
                                            rows = new object[]
                                            {
                                                new
                                                {
                                                    mode = "even",
                                                    widgets = new object[]
                                                    {
                                                        new
                                                        {
                                                            kind = "button",
                                                            action = "janus.github.refresh",
                                                            text = "刷新 GitHub",
                                                        },
                                                    },
                                                },
                                            },
                                        },
                                        new
                                        {
                                            type = "table",
                                            id = "github-rows",
                                            dataSource = new
                                            {
                                                command = "janus.ui.data",
                                                args = new { view = "github" },
                                            },
                                            columns = new object[]
                                            {
                                                new { key = "item", title = "项", width = "120" },
                                                new { key = "value", title = "值", width = "*" },
                                            },
                                        },
                                    },
                                },
                            },
                        },
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
            // 「改谁」取选中行、「改成什么」取输入框——两者必须分开取。
            // 跟随框一开始等于选中行，但人改过之后就不再相等；两边都从输入框取的话，
            // 改名只能把项目改成它自己。
            new
            {
                id = "janus.project.rename",
                title = "改名",
                command = "janus.proj.rename",
                args = new Dictionary<string, string>
                {
                    ["name"] = SelectedProject,
                    ["new"] = "{project-name}",
                },
                danger = true,
                summary = "把选中项目的分支展示名与工作树目录改成「项目名」框里的值",
            },
            new
            {
                id = "janus.project.create",
                title = "新建",
                command = "janus.proj.create",
                args = new Dictionary<string, string>
                {
                    ["name"] = "{new-project}",
                    // 以当前选中的项目为模板继承一个新项目。
                    ["base"] = SelectedProject,
                },
                summary = "以选中项目为模板新建「新项目名」框里的项目",
            },
            new
            {
                id = "janus.project.commit",
                title = "提交当前项目",
                command = "janus.proj.commit",
                args = new Dictionary<string, string>
                {
                    ["name"] = SelectedProject,
                    ["msg"] = "{commit-message}",
                },
                summary = "把选中项目按「提交描述」提交到本地仓库",
            },
            new
            {
                id = "janus.project.push",
                title = "推送当前项目",
                command = "janus.proj.push",
                args = new Dictionary<string, string>
                {
                    ["name"] = SelectedProject,
                },
                summary = "推送选中项目的分支",
            },
            // 刷新落到 Aurora 的取数刷新台账，而不是把 janus.gitrule.list 打到控制台。
            // 后者是这两个按钮 5.4.4 之前的样子：指令跑了，界面上那张表一动不动。
            //
            // 5.4.6 起两条都**按节点**刷，不再按页。三块内容收进「项目操作」一页之后，
            // 按页刷会把没被点到的那两块一起带上，而 GitHub 那条要探 SSH 与凭据助手。
            new
            {
                id = "janus.rules.refresh",
                title = "刷新规则",
                // 规则那一支有两张表，而 refreshdata 一次只收一个节点，所以过一道自己的指令。
                command = "janus.ui.refreshrules",
                summary = "重新读取排除清单与当前项目的落地状态",
            },
            new
            {
                id = "janus.github.refresh",
                title = "刷新 GitHub",
                command = "aurora.ui.refreshdata",
                args = new Dictionary<string, string> { ["node"] = "github-rows" },
                summary = "重新探测 Git、GCM、提交身份、origin 与 SSH",
            },
            new
            {
                id = "janus.graph.node.detail",
                title = "查看提交",
                command = "janus.ui.graphnode",
                args = new Dictionary<string, string> { ["node"] = "{node}" },
                summary = "查看图谱节点详情",
            },
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
