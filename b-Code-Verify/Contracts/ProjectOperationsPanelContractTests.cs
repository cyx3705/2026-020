using System.Text.Json;
using System.Text.RegularExpressions;
using HistoryJanus.Module;
using Xunit;

namespace HistoryJanus.Contracts;

/// <summary>
/// 「项目操作」控制面板与项目选择通道的接线（Aurora REQ-UI-041/043）。
///
/// **为什么要在 Janus 这一侧钉住**：这条链路的两端分属两个仓——通道名、控件 id 和动作 id
/// 由 Janus 写，解释它们的是 Aurora。写错一个字的症状不是报错，而是
/// 「按钮永远灰着」或者「改名把项目改成了它自己」，两者在界面上都看不出是拼错了。
/// Aurora 那边的门禁只能证明机制成立，证明不了本仓这份声明前后一致。
/// </summary>
public sealed partial class ProjectOperationsPanelContractTests
{
    private const string Channel = "janus.project";

    private static JsonElement Description
        => JsonDocument.Parse(HistoryJanusUiCommands.Description).RootElement;

    private static JsonElement Actions
        => JsonDocument.Parse(HistoryJanusUiCommands.ActionDeclarations).RootElement;

    private static JsonElement Page(string id)
        => Description.GetProperty("pages").EnumerateArray()
            .Single(page => page.GetProperty("id").GetString() == id);

    /// <summary>
    /// 项目表必须声明通道。漏了这一条，面板那边一个错都不会报——
    /// 按钮就是一直灰着，与「还没选中」完全一样。
    /// </summary>
    [Fact]
    public void TheProjectTablePublishesItsSelectionOnTheProjectChannel()
    {
        var table = Page("overview").GetProperty("content").GetProperty("children")
            .EnumerateArray()
            .Single(node => node.GetProperty("type").GetString() == "table");

        Assert.Equal(Channel, table.GetProperty("channel").GetString());

        // follows 与 {selection.*} 取的都是 name 列，因此它必须真的在列声明里。
        Assert.Contains(
            table.GetProperty("columns").EnumerateArray(),
            column => column.GetProperty("key").GetString() == "name");
    }

    /// <summary>
    /// 5.16.0 版面：项目总览竖着停在左侧，项目操作占中间成为主页面，图谱仍在底部。
    /// 这只是初次落位——用户保存过的场景布局优先，要看到新落位得重置场景。
    /// </summary>
    [Fact]
    public void TheOverviewSitsOnTheLeftAndOperationsTakeTheCenter()
    {
        Assert.Equal("left", Page("overview").GetProperty("placement").GetProperty("side").GetString());
        Assert.Equal("center", Page("projops").GetProperty("placement").GetProperty("side").GetString());
        Assert.Equal("bottom", Page("graph").GetProperty("placement").GetProperty("side").GetString());
    }

    /// <summary>
    /// 两行（5.16.0）：第一行项目名 + 改名、新项目名 + 新建；第二行提交、推送与三个弹窗子页，均布。
    /// 提交描述行删了：提交先摆差异，再弹窗要描述。
    ///
    /// **行是声明出来的**（Aurora 协议 V3 / REQ-UI-060），判「rows 数组长什么样」最接近人看到的东西。
    /// </summary>
    [Fact]
    public void TheOperationsPanelIsTwoDeclaredRows()
    {
        var rows = OperationsPanel().GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);

        var first = Widgets(rows[0]).ToList();
        Assert.Equal(
            new[] { "project-name", "new-project" },
            first.Where(w => w.GetProperty("kind").GetString() == "textbox")
                .Select(w => w.GetProperty("id").GetString()).ToArray());
        // 一行两个 flex 只认第一个：「项目名」吃余量，「新项目名」靠最窄宽度。
        // 两个都不写的话可变的会是最右边那个「新建」按钮，被拉成一条横杠。
        Assert.True(first[0].GetProperty("flex").GetBoolean(), "项目名框必须是可变宽度元素");
        Assert.False(first[2].TryGetProperty("flex", out _), "一行只能有一个 flex");
        Assert.True(first[2].GetProperty("minWidth").GetInt32() >= 120);

        Assert.Equal("even", rows[1].GetProperty("mode").GetString());
        Assert.All(Widgets(rows[1]), w => Assert.Equal("button", w.GetProperty("kind").GetString()));

        Assert.Equal(
            new[] { "改名", "新建", "提交", "推送", "入库规则", "LFS 规则", "GitHub" },
            rows.SelectMany(Widgets)
                .Where(w => w.GetProperty("kind").GetString() == "button")
                .Select(b => b.GetProperty("text").GetString())
                .ToArray());
        Assert.DoesNotContain(PanelWidgets(OperationsPanel()),
            w => w.TryGetProperty("id", out var id) && id.GetString() == "commit-message");
    }

    /// <summary>
    /// 只有「项目名」跟随选中行。「新项目名」是自由输入——
    /// 给它加上 follows 的话，每换一次选中就把人正在打的字冲掉。
    /// </summary>
    [Fact]
    public void OnlyTheProjectNameBoxFollowsTheSelection()
    {
        var following = PanelWidgets(OperationsPanel())
            .Where(w => w.TryGetProperty("follows", out _))
            .Select(w => (w.GetProperty("id").GetString(), w.GetProperty("follows").GetString()))
            .ToList();

        Assert.Equal([("project-name", Channel + ".name")], following);
    }

    /// <summary>
    /// 按钮全部按「有没有选中项目」启停。少写一个的后果不是不好看：
    /// 没选中时点下去，指令会收到一个空的 name 参数（LFS、GitHub 弹窗会是一张空表）。
    /// </summary>
    [Fact]
    public void EveryButtonIsGatedOnHavingASelectedProject()
    {
        var buttons = PanelWidgets(OperationsPanel())
            .Where(w => w.GetProperty("kind").GetString() == "button")
            .ToList();

        Assert.All(buttons, button => Assert.Equal(
            Channel,
            button.GetProperty("enabledWhen").GetProperty("selected").GetString()));
    }

    /// <summary>
    /// 面板里不得出现 <c>required</c>：Aurora 协议 V3（REQ-UI-060）把这个字段退役了，
    /// 写了不会报错，只会被静默忽略，那正是最难发现的一类不一致。
    /// </summary>
    [Fact]
    public void NoWidgetIsMarkedRequiredBecauseThatFieldIsRetired()
    {
        Assert.DoesNotContain(
            PanelWidgets(OperationsPanel()),
            widget => widget.TryGetProperty("required", out _));
    }

    /// <summary>
    /// 「改谁」与「改成什么」必须取自不同的地方。
    ///
    /// 跟随框一开始等于选中行，人改过之后就不再相等。两边都从 <c>{project-name}</c> 取的话，
    /// 改名会把项目改成它自己——指令成功、界面无异常、什么都没发生。
    /// </summary>
    [Fact]
    public void RenameTakesTheTargetFromTheSelectionAndTheNewNameFromTheBox()
    {
        var rename = Action("janus.project.rename");

        Assert.Equal("janus.proj.rename", rename.GetProperty("command").GetString());
        Assert.Equal(
            "{selection." + Channel + ".name}",
            rename.GetProperty("args").GetProperty("name").GetString());
        Assert.Equal("{project-name}", rename.GetProperty("args").GetProperty("new").GetString());
    }

    /// <summary>新建以选中项目为模板，名字取自「新项目名」框——两个参数不能反。</summary>
    [Fact]
    public void CreateInheritsFromTheSelectedProject()
    {
        var create = Action("janus.project.create");

        Assert.Equal("janus.proj.create", create.GetProperty("command").GetString());
        Assert.Equal("{new-project}", create.GetProperty("args").GetProperty("name").GetString());
        Assert.Equal(
            "{selection." + Channel + ".name}",
            create.GetProperty("args").GetProperty("base").GetString());
    }

    /// <summary>
    /// 提交与推送都作用在选中项目上。提交不带 msg（5.16.0）：
    /// 带着一个取不到的 {commit-message}，整条动作会被 Aurora 拒绝执行。
    /// </summary>
    [Fact]
    public void CommitAndPushActOnTheSelectedProject()
    {
        var commit = Action("janus.project.commit");
        Assert.Equal("janus.ui.projectaction", commit.GetProperty("command").GetString());
        Assert.Equal("提交", commit.GetProperty("args").GetProperty("action").GetString());
        Assert.Equal(
            "{selection." + Channel + ".name}",
            commit.GetProperty("args").GetProperty("name").GetString());
        Assert.False(commit.GetProperty("args").TryGetProperty("msg", out _));

        var push = Action("janus.project.push");
        Assert.Equal("janus.ui.projectaction", push.GetProperty("command").GetString());
        Assert.Equal("推送", push.GetProperty("args").GetProperty("action").GetString());
        Assert.Equal(
            "{selection." + Channel + ".name}",
            push.GetProperty("args").GetProperty("name").GetString());
    }

    /// <summary>
    /// 全仓自洽：按钮、单元格与行操作引用的动作都存在，动作里的占位符都指得着东西。
    ///
    /// 这条是前面几条的兜底——以后再加一行，忘了写动作或写错控件 id，这里就红。
    /// 判据分两类：<c>{selection.*}</c> 必须指向真的声明过的通道，
    /// 其余必须是面板里真的存在的控件 id，或某张表真的有的行字段。
    /// </summary>
    [Fact]
    public void EveryButtonResolvesAndEveryPlaceholderPointsAtSomethingReal()
    {
        var declared = Actions.GetProperty("actions").EnumerateArray()
            .Select(action => action.GetProperty("id").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        var nodes = AllNodes().ToList();
        var widgets = nodes
            .Where(node => node.TryGetProperty("rows", out _))
            .SelectMany(PanelWidgets).ToList();
        var controls = widgets
            .Where(w => w.GetProperty("kind").GetString() == "textbox")
            .Select(w => w.GetProperty("id").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        var rowFields = nodes
            .Where(node => node.TryGetProperty("columns", out _))
            .SelectMany(node => node.GetProperty("columns").EnumerateArray())
            .Select(column => column.GetProperty("key").GetString()!)
            // 不显示但在行里的字段：总览的 lifecycleAction、LFS 表的 name/next、GitHub 表的 name、分支表的 project。
            .Concat(["lifecycleAction", "name", "next", "project"])
            .ToHashSet(StringComparer.Ordinal);

        var bound = widgets.Where(w => w.GetProperty("kind").GetString() == "button")
            .Select(button => button.GetProperty("action").GetString()!)
            .Concat(nodes.Where(node => node.TryGetProperty("columns", out _))
                .SelectMany(node => node.GetProperty("columns").EnumerateArray())
                .SelectMany(column => new[] { "cellAction", "cellEditAction" }
                    .Where(key => column.TryGetProperty(key, out _))
                    .Select(key => column.GetProperty(key).GetString()!)))
            .Concat(nodes.Where(node => node.TryGetProperty("rowActions", out _))
                .SelectMany(node => node.GetProperty("rowActions").EnumerateArray())
                .Select(rowAction => rowAction.GetProperty("action").GetString()!));
        foreach (var id in bound)
            Assert.True(declared.Contains(id), $"界面绑了未声明的动作: {id}");

        foreach (var action in Actions.GetProperty("actions").EnumerateArray())
        {
            if (!action.TryGetProperty("args", out var args))
                continue;

            foreach (var argument in args.EnumerateObject())
            {
                foreach (Match match in Placeholder().Matches(argument.Value.GetString() ?? ""))
                {
                    var name = match.Groups[1].Value;
                    if (name.StartsWith("selection.", StringComparison.Ordinal))
                    {
                        Assert.StartsWith(
                            "selection." + Channel + ".",
                            name,
                            StringComparison.Ordinal);
                        continue;
                    }

                    // {node} 由泳道组件在点击时提供，{value} 是行内编辑的新值，都不是面板控件。
                    if (name is "node" or "value")
                        continue;

                    if (rowFields.Contains(name))
                        continue;

                    Assert.True(
                        controls.Contains(name),
                        $"动作 {action.GetProperty("id").GetString()} 引用了面板里没有的控件 {{{name}}}");
                }
            }
        }
    }

    [Fact]
    public void OverviewIsATwoRowControlPanelAndThreeClickableColumns()
    {
        // V5.6：页签叫 Janus；5.15.3 加「排列」；5.16.0 竖着放在左侧，四件分两行。
        Assert.Equal("Janus", Page("overview").GetProperty("title").GetString());
        var children = Page("overview").GetProperty("content").GetProperty("children")
            .EnumerateArray().ToList();
        Assert.DoesNotContain(children, node => node.GetProperty("type").GetString() == "text");

        var panel = children.Single(node => node.GetProperty("type").GetString() == "panel");
        var rows = panel.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, Widgets(rows[0]).Count());
        Assert.Equal(2, Widgets(rows[1]).Count());
        var widgets = PanelWidgets(panel).ToList();

        var refresh = widgets[0];
        Assert.Equal("刷新", refresh.GetProperty("text").GetString());
        Assert.Equal("refresh-cw", refresh.GetProperty("icon").GetString());
        Assert.Equal("janus.projects.refresh", refresh.GetProperty("action").GetString());

        var query = widgets[1];
        Assert.Equal("搜索", query.GetProperty("label").GetString());
        Assert.Equal("janus.overview.query", query.GetProperty("channel").GetString());
        Assert.True(query.GetProperty("flex").GetBoolean());

        var year = widgets[2];
        Assert.Equal("年份", year.GetProperty("label").GetString());
        Assert.Equal("select", year.GetProperty("mode").GetString());
        Assert.Equal("janus.overview.year", year.GetProperty("channel").GetString());
        Assert.Equal(
            "years",
            year.GetProperty("optionsSource").GetProperty("args").GetProperty("view").GetString());

        // 「排列」两态：第一态是原有的编号次序（选项框初值落在第一项）。
        var sort = widgets[3];
        Assert.Equal("排列", sort.GetProperty("label").GetString());
        Assert.Equal("select", sort.GetProperty("mode").GetString());
        Assert.Equal("janus.overview.sort", sort.GetProperty("channel").GetString());
        Assert.Equal(
            new[] { HistoryJanusUiCommands.SortByNumber, HistoryJanusUiCommands.SortByAction },
            sort.GetProperty("options").EnumerateArray().Select(item => item.GetString()));

        // 表格按通道取数：通道一变 Aurora 就重取，过滤不必自己写刷新。
        // refresh 不在参数里——它由「刷新」按钮显式立旗，见 janus.ui.refreshprojects。
        var table = children.Single(node => node.GetProperty("type").GetString() == "table");
        var args = table.GetProperty("dataSource").GetProperty("args");
        Assert.Equal("{selection.janus.overview.query.value}", args.GetProperty("query").GetString());
        Assert.Equal("{selection.janus.overview.year.value}", args.GetProperty("year").GetString());
        Assert.Equal("{selection.janus.overview.sort.value}", args.GetProperty("sort").GetString());
        Assert.False(args.TryGetProperty("refresh", out _));

        // 「最近提交」列删了（5.16.0）。
        var columns = table.GetProperty("columns").EnumerateArray().ToList();
        Assert.Equal(new[] { "项目", "z 级文件夹", "操作" },
            columns.Select(column => column.GetProperty("title").GetString()).ToArray());
        // 单击项目名打开项目目录（Aurora 表格没有双击事件）。
        Assert.Equal("janus.project.open", columns[0].GetProperty("cellAction").GetString());
        var open = Action("janus.project.open");
        Assert.Equal("janus.proj.open", open.GetProperty("command").GetString());
        Assert.Equal("{name}", open.GetProperty("args").GetProperty("name").GetString());
        Assert.Equal("janus.project.openmeta", columns[1].GetProperty("cellAction").GetString());

        Assert.Equal("janus.project.action", columns[2].GetProperty("cellAction").GetString());
        Assert.Equal("status", columns[2].GetProperty("key").GetString());
        Assert.Equal("120", columns[2].GetProperty("width").GetString());
    }

    [Fact]
    public void EveryPageCommandArgumentUsesTheAuroraStringMapContract()
    {
        var dataSources = AllNodes()
            .Where(node => node.TryGetProperty("dataSource", out _))
            .Select(node => node.GetProperty("dataSource"));

        foreach (var dataSource in dataSources)
        {
            if (!dataSource.TryGetProperty("args", out var args))
                continue;

            foreach (var argument in args.EnumerateObject())
            {
                Assert.Equal(
                    JsonValueKind.String,
                    argument.Value.ValueKind);
            }
        }
    }

    /// <summary>
    /// 退役的页面节点一个都不许再出现。1.8.14 起 <c>button</c> / <c>input</c> / <c>select</c>
    /// 不再是页面节点，写了会渲染成一块写着原因的牌子——而牌子是能用的界面的反面。
    /// </summary>
    [Fact]
    public void NoPageStillUsesARetiredNodeType()
    {
        var raw = HistoryJanusUiCommands.Description;

        foreach (var retired in new[] { "\"type\":\"button\"", "\"type\":\"input\"", "\"type\":\"select\"" })
            Assert.DoesNotContain(retired, raw, StringComparison.Ordinal);

        // 面板的 orientation 与 inline 随协议 V3 一同退役（Aurora REQ-UI-060）。
        var panels = AllNodes()
            .Where(node => node.TryGetProperty("type", out var type) && type.GetString() == "panel")
            .ToList();

        Assert.NotEmpty(panels);
        foreach (var panel in panels)
        {
            Assert.False(panel.TryGetProperty("orientation", out _), "面板还写着已退役的 orientation");
            Assert.DoesNotContain(
                PanelWidgets(panel),
                widget => widget.TryGetProperty("inline", out _));
        }
    }

    /// <summary>
    /// 入库规则、LFS 规则、GitHub 是三张平时不露面的页（5.16.0），由项目操作页第二行的按钮浮成弹窗。
    ///
    /// 5.4.6～5.15 它们收在项目操作页的轮换选项框 + switch 里；5.16.0 起那一套整个拆掉，
    /// 不能留半截：选项框还在而 switch 没了，下面永远是空的，不报错。
    /// 按钮文字、页标题、opensection 的参数值必须是同一个字符串——三处各写各的就会有一处对不上。
    /// </summary>
    [Fact]
    public void TheThreeSectionsArePopupPagesOpenedByButtons()
    {
        Assert.Equal(
            new[] { "overview", "graph", "projops", "janus-rules", "janus-lfs", "janus-github" },
            Description.GetProperty("pages").EnumerateArray()
                .Select(page => page.GetProperty("id").GetString())
                .ToArray());
        Assert.DoesNotContain(AllNodes(),
            node => node.TryGetProperty("type", out var type) && type.GetString() == "switch");

        foreach (var (pageId, actionId) in new[]
                 {
                     (HistoryJanusUiCommands.RulesPage, "janus.section.rules"),
                     (HistoryJanusUiCommands.LfsPage, "janus.section.lfs"),
                     (HistoryJanusUiCommands.GitHubPage, "janus.section.github"),
                 })
        {
            var page = Page(pageId);
            Assert.False(page.GetProperty("placement").GetProperty("visible").GetBoolean(), $"{pageId} 平时不露面");
            var title = page.GetProperty("title").GetString();

            var button = PanelWidgets(OperationsPanel())
                .Single(w => w.TryGetProperty("action", out var a) && a.GetString() == actionId);
            Assert.Equal(title, button.GetProperty("text").GetString());

            var action = Action(actionId);
            Assert.Equal("janus.ui.opensection", action.GetProperty("command").GetString());
            Assert.Equal(title, action.GetProperty("args").GetProperty("section").GetString());
        }
    }

    /// <summary>
    /// 页面顶部那几段"这一页是干什么的"说明文字全部删掉：占版面，给的是一次性的信息。
    /// </summary>
    [Fact]
    public void NoPageStillCarriesAPreambleParagraph()
    {
        var preambles = AllNodes()
            .Where(node => node.TryGetProperty("type", out var type) && type.GetString() == "text")
            .Select(node => node.GetProperty("text").GetString() ?? "")
            .ToList();

        Assert.All(preambles, text => Assert.True(
            text.Length <= 12,
            $"页面里还留着一段说明文字：{text}"));
    }

    /// <summary>
    /// 会跑 Git 的取数一律按**单个项目**取，项目名从选择通道来。
    ///
    /// 落地状态要对每个项目跑两条 <c>git ls-files</c>，全库一遍是 90 次进程启动；
    /// 分支、历史与图谱同理。这些绝不能由「打开一个页签」触发。
    /// </summary>
    [Fact]
    public void EveryGitBackedViewIsScopedToTheSelectedProject()
    {
        foreach (var (node, view) in new[]
                 {
                     ("branch-rows", "branches"),
                     ("history-rows", "history"),
                     ("github-rows", "github"),
                     ("lfs-stat", "lfsstat"),
                     ("lfs-files", "lfs"),
                 })
        {
            var args = Node(node).GetProperty("dataSource").GetProperty("args");
            Assert.Equal(view, args.GetProperty("view").GetString());
            Assert.Equal("{selection." + Channel + ".name}", args.GetProperty("name").GetString());
        }

        // 图谱同样跟着选中走。此前它固定画项目清单的第一条——不报错，只是一直不对。
        var graph = Page("graph").GetProperty("content").GetProperty("dataSource").GetProperty("args");
        Assert.Equal("{selection." + Channel + ".name}", graph.GetProperty("name").GetString());

        // 入库规则是全库共用的清单，**不带项目名**——带了就会在每次换项目时白白重取一次。
        var rules = Node("rule-list").GetProperty("dataSource").GetProperty("args");
        Assert.Equal("excludes", rules.GetProperty("view").GetString());
        Assert.False(rules.TryGetProperty("name", out _), "入库规则与选中项目无关");
    }

    /// <summary>
    /// 分支控制（5.16.0）：分支名可点（下面的历史换成它），四种操作只进右键菜单。
    ///
    /// 历史表的取数参数里**没有**分支：要看哪条由模块记着。按通道取的话，
    /// 表格不会自动选中首行，没点过分支之前历史表永远是空的。
    /// </summary>
    [Fact]
    public void TheBranchTableDrivesHistoryAndCarriesARightClickMenu()
    {
        var table = Node("branch-rows");
        var columns = table.GetProperty("columns").EnumerateArray().ToList();
        Assert.Equal("branch", columns[0].GetProperty("key").GetString());
        Assert.Equal("janus.branch.pick", columns[0].GetProperty("cellAction").GetString());
        Assert.Equal("janus.ui.branchpick", Action("janus.branch.pick").GetProperty("command").GetString());

        var menu = table.GetProperty("rowActions").EnumerateArray().ToList();
        Assert.Equal(
            new[] { "janus.branch.switch", "janus.branch.merge", "janus.branch.create", "janus.branch.delete" },
            menu.Select(item => item.GetProperty("action").GetString()).ToArray());
        Assert.All(menu, item => Assert.False(item.GetProperty("inline").GetBoolean(), "分支操作只进右键菜单"));

        foreach (var item in menu)
        {
            var action = Action(item.GetProperty("action").GetString()!);
            Assert.Equal("janus.ui.branchop", action.GetProperty("command").GetString());
            var args = action.GetProperty("args");
            Assert.Equal("{project}", args.GetProperty("name").GetString());
            Assert.Equal("{branch}", args.GetProperty("branch").GetString());
            Assert.EndsWith("." + args.GetProperty("op").GetString(), action.GetProperty("id").GetString());
        }

        // 分支表在历史表上面：先选分支，再看它的历史。
        var order = Page("projops").GetProperty("content").GetProperty("children").EnumerateArray()
            .Select(node => node.TryGetProperty("id", out var id) ? id.GetString() : null).ToList();
        Assert.Equal(new[] { "janus-projops", "branch-rows", "history-rows" }, order.ToArray());
        Assert.False(Node("history-rows").GetProperty("dataSource").GetProperty("args").TryGetProperty("branch", out _));
    }

    /// <summary>
    /// LFS 弹窗：上面一行汇总（合规、指针大小、≥100MB），下面文件、大小、操作三列，操作是多态按钮。
    /// 5.16.0 起汇总是表格的一行：表格能按节点重取，选择框只在通道变化时重取。
    /// </summary>
    [Fact]
    public void TheLfsPopupIsAStatRowAndAThreeColumnTable()
    {
        var page = Page(HistoryJanusUiCommands.LfsPage).GetProperty("content");
        Assert.Equal(
            new[] { "lfs-stat", "lfs-files" },
            page.GetProperty("children").EnumerateArray().Select(n => n.GetProperty("id").GetString()).ToArray());

        Assert.Equal(
            new[] { "compliance", "bytes", "oversize" },
            Node("lfs-stat").GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("key").GetString()).ToArray());

        var table = Node("lfs-files");
        Assert.Equal(
            new[] { "path", "size", "op" },
            table.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("key").GetString()).ToArray());
        Assert.False(table.TryGetProperty("rowActions", out _), "去向只由操作格切换，不再挂行操作");
        var op = table.GetProperty("columns").EnumerateArray().Last();
        Assert.Equal("janus.lfs.toggle", op.GetProperty("cellAction").GetString());
        Assert.Equal("{next}", Action("janus.lfs.toggle").GetProperty("args").GetProperty("decision").GetString());
    }

    // REQ-018：远端在原表格中编辑，提交使用行快照的项目名，不能取后来切换的选择通道。
    [Fact]
    public void GitHubRemoteEditorLivesInTheTableAndSavesTheRowProject()
    {
        var table = Node("github-rows");
        Assert.Equal("table", table.GetProperty("type").GetString());
        var value = table.GetProperty("columns").EnumerateArray().Single(c => c.GetProperty("key").GetString() == "value");
        Assert.Equal("janus.github.setremote", value.GetProperty("cellEditAction").GetString());
        Assert.Equal("editable", value.GetProperty("editableWhen").GetString());
        Assert.DoesNotContain(AllNodes(),
            node => node.TryGetProperty("id", out var id) && id.GetString() == "github-remote-panel");
        var action = Action("janus.github.setremote");
        Assert.Equal("janus.ui.setremote", action.GetProperty("command").GetString());
        Assert.Equal("{name}", action.GetProperty("args").GetProperty("name").GetString());
        Assert.Equal("{value}", action.GetProperty("args").GetProperty("url").GetString());
    }

    /// <summary>一个面板里的全部小组件，按行内顺序摊平。</summary>
    private static List<JsonElement> PanelWidgets(JsonElement panel)
        => panel.GetProperty("rows").EnumerateArray().SelectMany(Widgets).ToList();

    /// <summary>一行里的小组件。</summary>
    private static IEnumerable<JsonElement> Widgets(JsonElement row)
        => row.GetProperty("widgets").EnumerateArray();

    /// <summary>全部页面里 id 为 <paramref name="nodeId"/> 的那一个节点（节点 id 跨页唯一）。</summary>
    private static JsonElement Node(string nodeId)
        => AllNodes().Single(node => node.TryGetProperty("id", out var id) && id.GetString() == nodeId);

    private static IEnumerable<JsonElement> AllNodes()
        => Description.GetProperty("pages").EnumerateArray()
            .SelectMany(page => Descend(page.GetProperty("content")));

    /// <summary>页面内容树的全部节点，含自己。</summary>
    private static IEnumerable<JsonElement> Descend(JsonElement node)
    {
        yield return node;
        if (!node.TryGetProperty("children", out var children))
            yield break;
        foreach (var child in children.EnumerateArray())
        {
            foreach (var nested in Descend(child))
                yield return nested;
        }
    }

    private static JsonElement OperationsPanel()
        => Page("projops").GetProperty("content").GetProperty("children")
            .EnumerateArray()
            .Single(node => node.GetProperty("type").GetString() == "panel");

    private static JsonElement Action(string id)
        => Actions.GetProperty("actions").EnumerateArray()
            .Single(action => action.GetProperty("id").GetString() == id);

    [GeneratedRegex(@"\{([^{}\s]+)\}")]
    private static partial Regex Placeholder();
}
