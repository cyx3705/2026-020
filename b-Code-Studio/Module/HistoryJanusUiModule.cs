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

internal static partial class HistoryJanusUiCommands
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

    /// <summary>
    /// 四个子页面的标题。它们同时是选项框的候选项和 switch 的 case，只写一处。
    ///
    /// 5.11.0 起「Git 文件规则」拆成两页：入库规则（全库共用的不纳入仓库清单）
    /// 与 LFS 规则（按选中项目列出指针文件与 ≥100MB 文件，逐个定去向）。
    /// 两者原先挤在一张表里，靠「类型」一列区分，而它们回答的是两个问题。
    /// </summary>
    private const string SectionRules = "入库规则";

    private const string SectionLfs = "LFS 规则";

    private const string SectionHistory = "分支历史";

    private const string SectionGitHub = "GitHub";

    /// <summary>
    /// 总览页顶栏的两个筛选通道。搜索框与年份框各占一个；
    /// 表格的取数参数按通道取值，通道一变 Aurora 就重取，不必自己写刷新。
    /// </summary>
    private const string OverviewQueryChannel = "janus.overview.query";

    private const string OverviewYearChannel = "janus.overview.year";

    /// <summary>年份下拉里「不过滤」那一项。它同时是候选表的第一行和过滤器的短路值。</summary>
    private const string AllYears = "全部";

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
                    Description = "页面视图：projects、graph、history、excludes、lfs、lfsstat、github",
                    Required = true,
                    Position = 0,
                },
                new ParameterSpec
                {
                    Name = "name",
                    Description = "项目名；history / graph / lfs / lfsstat 需要，由页面按当前选中行填入",
                    Position = 1,
                },
                new ParameterSpec
                {
                    Name = "item",
                    Description = "lfsstat 视图取哪一格：compliance / pointers / bytes / oversize / lfs / ignore / undecided",
                },
                new ParameterSpec
                {
                    Name = "section",
                    Description = "lfsstat 视图不用它；页面带上它只为切回子页时触发重取",
                },
                new ParameterSpec
                {
                    Name = "refresh",
                    Description = "projects 视图是否查询远端",
                    Type = ParamType.Bool,
                    Default = "false",
                },
                new ParameterSpec
                {
                    Name = "query",
                    Description = "projects 视图的搜索词；匹配项目名与 z 级文件夹名",
                },
                new ParameterSpec
                {
                    Name = "year",
                    Description = $"projects 视图的年份过滤；「{AllYears}」或空表示不过滤",
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
            Name = "janus.ui.sectionenter",
            Domain = Domain,
            CommandClass = "ui",
            Summary = "切到某个子页面时重取它那一张表",
            Readonly = true,
            HiddenReason = "界面内部协议，对模型无意义",
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "section",
                    Description = $"子页面标题：{SectionRules} / {SectionLfs} / {SectionHistory} / {SectionGitHub}",
                    Required = true,
                    Position = 0,
                },
            ],
            Handler = context => EnterSectionAsync(context, bus),
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "janus.ui.lfsdecide",
            Domain = Domain,
            CommandClass = "ui",
            Summary = "LFS 规则表的行操作：记住一个 ≥100MB 文件的去向并刷新表格",
            HiddenReason = "界面内部协议；模型请用 janus.gitrule.lfsset",
            Parameters =
            [
                new ParameterSpec { Name = "name", Description = "项目名", Required = true, Position = 0 },
                new ParameterSpec { Name = "path", Description = "仓库内相对路径", Required = true, Position = 1 },
                new ParameterSpec { Name = "decision", Description = "lfs / ignore / none", Required = true, Position = 2 },
            ],
            Handler = context => DecideLfsAsync(context, bus),
        }, source);

        RegisterLifecycleCommands(registry, bus, source, business);
    }

    /// <summary>
    /// 子页面一被切到就重取它那一张表。
    ///
    /// 为什么非得由模块来做这件事：Aurora 的 switch 在建页时一次建好三支，
    /// 切走再切回来用的是**同一个控件实例**（为的是保住滚动位置、筛选词和选中行），
    /// 因此切回来不会触发取数。此前靠「刷新规则 / 刷新 GitHub」两个按钮补位，
    /// 等于把「这张表是不是旧的」这件事交给人判断。
    ///
    /// 按节点刷、不按页刷：一页上挂着三支，刷整页会把没人看的那两支一起跑掉。
    /// 认不出的标题按成功返回：子页面是界面自己的候选项，多出一个不该让人看见报错。
    /// </summary>
    private static async Task<CommandResult> EnterSectionAsync(CommandContext context, CommandBus bus)
    {
        string[] nodes = context.GetString("section")?.Trim() switch
        {
            SectionRules => ["rule-list"],
            // 汇总与文件表来自同一次检查；先作废缓存，两张表才会一起看到新的一次。
            SectionLfs => LfsNodes(),
            SectionHistory => ["history-rows"],
            SectionGitHub => ["github-rows"],
            _ => [],
        };
        if (nodes.Length == 0)
            return CommandResult.Ok("该子页面没有需要重取的表");
        var last = CommandResult.Ok("");
        foreach (var node in nodes)
            last = await bus.ExecuteAsync($"aurora.ui.refreshdata node={node}", context.Source, context.Cancellation);
        return last;
    }

    private static string[] LfsNodes()
    {
        // 面板那几格不是表格节点，refreshdata 够不着；它们靠通道变化重取。
        UiLfsProjection.Invalidate();
        return ["lfs-files"];
    }

    /// <summary>
    /// 行操作走这里而不是直接打 <c>janus.gitrule.lfsset</c>：记完决定要让两张表重取，
    /// 否则人点了「LFS 指针」，那一行的「决定」还写着「未决定」。
    /// </summary>
    private static async Task<CommandResult> DecideLfsAsync(CommandContext context, CommandBus bus)
    {
        var result = await bus.ExecuteAsync(
            $"janus.gitrule.lfsset name={CommandParser.QuoteArg(context.RequireString("name"))} " +
            $"path={CommandParser.QuoteArg(context.RequireString("path"))} " +
            $"decision={CommandParser.QuoteArg(context.RequireString("decision"))}",
            context.Source, context.Cancellation);
        foreach (var node in LfsNodes())
            await bus.ExecuteAsync($"aurora.ui.refreshdata node={node}", context.Source, context.Cancellation);
        return result;
    }

    private static async Task<CommandResult> LoadDataAsync(
        CommandContext context,
        CommandBus bus,
        Func<StudioBusinessComposition?> business)
    {
        var view = context.GetString("view")?.Trim().ToLowerInvariant();
        if (view == "graph")
            return await LoadGraphAsync(context, bus);

        // 排除清单存在设置里，读它不碰 Git。
        if (view == "excludes")
            return Rows(UiRuleProjection.Excludes(business()));

        // LFS 面板七格与文件表按选中项目取，共用一次检查（见 UiLfsProjection 的缓存）。
        if (view is "lfs" or "lfsstat")
        {
            var inspected = await UiLfsProjection.InspectAsync(business(), context.GetString("name"));
            return Rows(view == "lfs"
                ? UiLfsProjection.Files(inspected)
                : UiLfsProjection.Stat(inspected, context.GetString("item")));
        }

        if (view is "projects" or "years")
            return await LoadProjectsAsync(context, bus, view == "years");

        var command = view switch
        {
            // 这两条都按**单个项目**取，项目名由页面从选中通道填入。
            // 不带项目名就不取——全库扫描不该由"打开一个页签"触发。
            "history" => Scoped(context, "janus.history.list", " limit=200"),
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
            "history" => Rows(UiHistoryProjection.Read(result.Data)),
            "github" => Rows(UiGitHubProjection.Read(result.Data)),
            _ => Payload(JsonSerializer.Serialize(result.Data)),
        };
    }

    /// <summary>
    /// 项目清单的**唯一**读点：总览表格与年份候选都从这里出去。
    ///
    /// 为什么要缓存：搜索框与年份框一动，Aurora 就按新的参数重取一次表格——
    /// 而「取项目清单」这件事最贵的一版要 fetch 全部远端。每敲一个字符 fetch 一轮
    /// 是不能接受的，所以过滤只在**已经拿到的那份清单**上做，
    /// 真正的重取由「刷新」按钮显式发起（<see cref="_forceProjectRefresh"/>）。
    /// </summary>
    private static async Task<CommandResult> LoadProjectsAsync(
        CommandContext context,
        CommandBus bus,
        bool yearsOnly)
    {
        var projects = await ReadProjectsAsync(context, bus);
        if (projects.Failure != null)
            return projects.Failure;

        if (yearsOnly)
            return Rows(UiProjectProjection.YearOptions(projects.Items, AllYears));

        var filtered = UiProjectProjection.Filter(
            projects.Items,
            context.GetString("query"),
            context.GetString("year"),
            AllYears);
        return Payload(UiProjectProjection.Serialize(filtered));
    }

    /// <summary>
    /// 「重取一次远端」的一次性开关。
    ///
    /// 它不能做成取数参数：取数参数由页面描述写死，而「刷新」是一个**动作**，
    /// 动作能做的只有让某个节点重取（<c>aurora.ui.refreshdata</c>）。
    /// 于是刷新按钮先立这面旗，再让表格重取；重取时旗被取走，
    /// 后续因为搜索词变化而发生的重取一律走缓存。
    /// </summary>
    private static bool _forceProjectRefresh;

    private static IReadOnlyList<WorktreeInfo>? _projectCache;
    private static readonly SemaphoreSlim ProjectCacheGate = new(1, 1);

    internal static void RequestProjectRefresh() => _forceProjectRefresh = true;

    private static async Task<(IReadOnlyList<WorktreeInfo> Items, CommandResult? Failure)> ReadProjectsAsync(
        CommandContext context,
        CommandBus bus)
    {
        await ProjectCacheGate.WaitAsync(context.Cancellation);
        try
        {
            var forced = _forceProjectRefresh || context.GetBool("refresh");
            _forceProjectRefresh = false;
            if (!forced && _projectCache is { } cached)
                return (cached, null);

            // 首屏建树发生在宿主启动路径上，因此默认那一版不查远端也不读工作树状态；
            // 「刷新」按下的那一次才付全价。
            var command = forced
                ? "janus.proj.list status=true refresh=true"
                : "janus.proj.list status=false";
            var result = await bus.ExecuteAsync(command, context.Source, context.Cancellation);
            if (!result.Success)
                return ([], CommandResult.Fail(result.Message));

            var items = UiProjectProjection.ReadWorktrees(result.Data);
            _projectCache = items;
            // 只有界面（Aurora 取数来源是 UI）才补这一轮；冒烟与脚本取数不该顺带 fetch 全库。
            if (!forced && context.Source == "UI" && Interlocked.Exchange(ref _autoRefreshStarted, 1) == 0)
                _ = Task.Run(() => AutoRefreshProjectsAsync(bus, context.Source, items));
            return (items, null);
        }
        finally { ProjectCacheGate.Release(); }
    }

    /// <summary>本进程（本次模块加载）是否已经发起过首屏之后的那一轮远端刷新。</summary>
    private static int _autoRefreshStarted;

    /// <summary>
    /// 打开软件后自动补一轮「刷新」：首屏先用不查远端的便宜清单把表格立起来，
    /// 随后在后台线程 fetch 全部项目（不占界面线程与宿主启动路径），完成后让表格重取。
    ///
    /// 不借 <see cref="_forceProjectRefresh"/> 这面旗：它是给按钮用的一次性开关，
    /// 后台任务若立了旗却没等到重取，下一次因搜索词变化的取数就会意外付全价。
    /// 这期间人若已按过「刷新」，缓存已被换掉，这里就不再覆盖。
    /// </summary>
    private static async Task AutoRefreshProjectsAsync(
        CommandBus bus, string source, IReadOnlyList<WorktreeInfo> firstScreen)
    {
        try
        {
            var result = await bus.ExecuteAsync("janus.proj.list status=true refresh=true", source);
            if (!result.Success)
                return;
            var items = UiProjectProjection.ReadWorktrees(result.Data);

            await ProjectCacheGate.WaitAsync();
            try
            {
                if (!ReferenceEquals(_projectCache, firstScreen))
                    return;
                _projectCache = items;
            }
            finally { ProjectCacheGate.Release(); }

            _ = await bus.ExecuteAsync("aurora.ui.refreshdata node=projects", source);
        }
        catch (Exception)
        {
            // 后台补刷失败不影响首屏；表格仍显示「刷新 ?」，人可以自己点。
        }
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

    /// <summary>入库规则：全库共用的排除清单，与选中项目无关。</summary>
    internal static class UiRuleProjection
    {
        /// <summary>
        /// 先列入库例外，再逐条列不入库的目录与扩展名。
        /// 5.11.0 之前这张表开头还有本仓走 LFS 的文件；那是另一个问题，已搬到「LFS 规则」子页。
        /// </summary>
        public static IReadOnlyList<IReadOnlyDictionary<string, string>> Excludes(
            StudioBusinessComposition? business)
        {
            if (business is null)
                return [Row(("kind", "—"), ("rule", "业务组合尚未装配"))];

            var rules = business.GitRules;
            return new[] { Row(("kind", "入库"), ("rule", "z-* 目录始终入库")) }
                .Concat(rules.Directories.Select(directory => Row(("kind", "不入库"), ("rule", directory))))
                .Concat(rules.Suffixes.Select(suffix => Row(("kind", "不入库"), ("rule", suffix))))
                .ToList();
        }
    }

    /// <summary>
    /// LFS 规则：选中项目的 LFS 指针文件与 ≥100MB 文件，上面一张汇总、下面一张逐个文件。
    ///
    /// 两张表来自**同一次**检查——检查要跑 git lfs ls-files 并扫一遍工作区，
    /// 两张表各跑一遍既慢，又可能在两次之间看到不一样的仓。缓存只活几秒，
    /// 切子页与行操作都会先作废它。
    /// </summary>
    internal static class UiLfsProjection
    {
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(15);
        private static readonly object Gate = new();
        private static (string Project, DateTime At, Task<(bool Success, string Message, LfsInspection? Report)> Task)? _cache;

        public static void Invalidate()
        {
            lock (Gate)
                _cache = null;
        }

        public static Task<(bool Success, string Message, LfsInspection? Report)> InspectAsync(
            StudioBusinessComposition? business, string? project)
        {
            if (business is null)
                return Task.FromResult<(bool Success, string Message, LfsInspection? Report)>((false, "业务组合尚未装配", null));
            if (string.IsNullOrWhiteSpace(project))
                return Task.FromResult<(bool Success, string Message, LfsInspection? Report)>((false, "先在项目总览选中一个项目", null));
            lock (Gate)
            {
                if (_cache is { } hit && hit.Project.Equals(project, StringComparison.OrdinalIgnoreCase)
                                      && DateTime.UtcNow - hit.At < CacheLifetime)
                    return hit.Task;
                // 不带调用方的取消令牌：另一张表还在等同一个结果。
                var task = business.LfsRules.InspectAsync(project, CancellationToken.None);
                _cache = (project, DateTime.UtcNow, task);
                return task;
            }
        }

        /// <summary>
        /// 面板一格的值，作为**唯一一个候选**交给选择框。合规只给 ✓ / ✗。
        /// 取不到时给「—」而不是空：空候选会让选择框留着上一个项目的值。
        /// </summary>
        public static IReadOnlyList<IReadOnlyDictionary<string, string>> Stat(
            (bool Success, string Message, LfsInspection? Report) inspected, string? item)
        {
            if (!inspected.Success || inspected.Report is not { Available: true } report)
                return [Row(("value", "—"))];
            var value = item?.Trim().ToLowerInvariant() switch
            {
                "compliance" => report.SmallPointerCount + report.ForeignRuleCount == 0 ? "✓" : "✗",
                "pointers" => report.PointerCount.ToString(),
                "bytes" => LfsPolicy.FormatBytes(report.PointerBytes),
                "oversize" => report.OversizeCount.ToString(),
                "lfs" => report.DecidedLfs.ToString(),
                "ignore" => report.DecidedIgnore.ToString(),
                "undecided" => report.Undecided.ToString(),
                _ => "—",
            };
            return [Row(("value", value))];
        }

        /// <summary>
        /// 文件表三列：文件、大小、操作。「操作」是多态按钮的文字——当前决定加符号；
        /// <c>next</c> 是点一下要切到的去向，与 <c>name</c> 一起作为动作参数，不显示。
        /// 已被入库规则排除的文件不列：它们本来就不进仓库，与 LFS 无关。
        /// </summary>
        public static IReadOnlyList<IReadOnlyDictionary<string, string>> Files(
            (bool Success, string Message, LfsInspection? Report) inspected)
        {
            if (!inspected.Success || inspected.Report is not { Available: true } report)
                return [];
            return report.Files
                .Where(file => file.Decision != "—")
                .Select(file =>
                {
                    var (op, next) = file.Decision switch
                    {
                        "LFS 指针" => ("LFS 指针 ●", "ignore"),
                        "不纳入 git" => ("不纳入 ✕", "lfs"),
                        _ => ("未决定 ○", "lfs"),
                    };
                    return Row(
                        ("name", report.Project),
                        ("path", file.Path),
                        ("size", file.Size),
                        ("op", op),
                        ("next", next));
                })
                .ToList();
        }
    }

    /// <summary>分支历史：从分叉点到 HEAD 的自有提交。</summary>
    internal static class UiHistoryProjection
    {
        /// <summary>
        /// **HEAD 在最上面**。业务侧 <c>janus.history.list</c> 按「分叉点在前、
        /// 时间往后」返回——那是给人顺着读一条分支用的次序；页面上要先看见的是
        /// 最近几次提交，因此在投影这一层倒过来，业务命令的输出次序不动。
        ///
        /// 短 sha 与作者仍然带在行里：列表不显示它们（见页面描述），
        /// 但行数据是选中通道的载荷，别处按字段名取值，去掉会连带断掉。
        /// </summary>
        public static IReadOnlyList<IReadOnlyDictionary<string, string>> Read(object? data)
            => data is not BranchHistoryReport report
                ? []
                : report.Entries
                    .Reverse()
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

    /// <summary>
    /// 参数值的编码统一交给宿主解析器的反函数，不在模块里自写一份。
    ///
    /// 模块原先那一份只在「有空格或有引号」时加引号，且只转义引号：差异正文里的
    /// 反斜杠（Windows 路径、`\ No newline at end of file`）会被当成转义符，
    /// 而只含换行、不含空格的值根本不会被引起来——两种都是指令拼出来就散架。
    /// </summary>
    private static string Quote(string value) => CommandParser.QuoteArg(value);
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
