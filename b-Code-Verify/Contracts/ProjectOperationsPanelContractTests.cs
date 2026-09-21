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

    /// <summary>子页面通道（5.4.6）。选项框往这里发布当前标题，switch 容器按它选分支。</summary>
    private const string SectionChannel = "janus.section";

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
    /// 四行：项目名+改名、新项目名+新建、提交描述+提交/推送，外加 5.4.6 的子页面切换器。
    ///
    /// **行现在是声明出来的**（Aurora 协议 V3 / REQ-UI-060），不再由 <c>inline</c> 副产。
    /// 这条因此改判「rows 数组长什么样」——比数按钮上的 inline 标志更接近人看到的东西。
    /// </summary>
    [Fact]
    public void TheOperationsPanelIsFourDeclaredRows()
    {
        var rows = OperationsPanel().GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(4, rows.Count);

        // 前三行：一个文本框加一到两个按钮，文本框吃余量。
        Assert.Equal(
            new[] { "project-name", "new-project", "commit-message" },
            rows.Take(3).Select(row => Widgets(row)
                .Single(w => w.GetProperty("kind").GetString() == "textbox")
                .GetProperty("id").GetString()).ToArray());

        // 中间那个文本框是可变宽度元素。不写的话可变的会是**最右边**那个按钮，
        // 于是输入框停在最窄宽度、按钮被拉成一条横杠——版面上一眼看得出，
        // 但没有任何一处会报错。
        Assert.All(
            rows.Take(3),
            row => Assert.True(
                Widgets(row).Single(w => w.GetProperty("kind").GetString() == "textbox")
                    .GetProperty("flex").GetBoolean(),
                "前三行的文本框必须是可变宽度元素"));

        // 第三行两个按钮：提交、推送。
        Assert.Equal(
            new[] { "改名", "新建", "提交", "推送" },
            rows.SelectMany(Widgets)
                .Where(w => w.GetProperty("kind").GetString() == "button")
                .Select(b => b.GetProperty("text").GetString())
                .ToArray());

        // 第四行是子页面切换器，均布。它排在**最后**：管的是自己下面那块内容，
        // 排在前面的话，人得隔着三行项目操作才把「选项框」和「下面变了」联系起来。
        var last = rows[3];
        Assert.Equal("even", last.GetProperty("mode").GetString());
        Assert.Equal(
            "section",
            Assert.Single(Widgets(last)).GetProperty("id").GetString());
    }

    /// <summary>
    /// 只有「项目名」跟随选中行。「新项目名」和「提交描述」是自由输入——
    /// 给它们加上 follows 的话，每换一次选中就把人正在打的字冲掉。
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
    /// 四个按钮全部按「有没有选中项目」启停。少写一个的后果不是不好看：
    /// 没选中时点下去，指令会收到一个空的 name 参数。
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
    /// 面板里不得出现 <c>required</c>。
    ///
    /// 这条本仓先立、Aurora 后收：面板的必填校验当年是**全局**的——任何一个必填框为空，
    /// 面板上每个按钮都拒绝执行，而报出来的错说的是「新项目名为必填项」，
    /// 看上去与改名毫无关系。这份声明因此一直不敢用它，理由写在注释里。
    /// Aurora 协议 V3（REQ-UI-060）把这个字段整个退役了，断言留着当**回归闸**：
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

    /// <summary>提交与推送都作用在选中项目上；提交描述取自面板。</summary>
    [Fact]
    public void CommitAndPushActOnTheSelectedProject()
    {
        var commit = Action("janus.project.commit");
        Assert.Equal("janus.ui.projectaction", commit.GetProperty("command").GetString());
        Assert.Equal("提交", commit.GetProperty("args").GetProperty("action").GetString());
        Assert.Equal(
            "{selection." + Channel + ".name}",
            commit.GetProperty("args").GetProperty("name").GetString());
        Assert.Equal("{commit-message}", commit.GetProperty("args").GetProperty("msg").GetString());

        var push = Action("janus.project.push");
        Assert.Equal("janus.ui.projectaction", push.GetProperty("command").GetString());
        Assert.Equal("推送", push.GetProperty("args").GetProperty("action").GetString());
        Assert.Equal(
            "{selection." + Channel + ".name}",
            push.GetProperty("args").GetProperty("name").GetString());
    }

    /// <summary>
    /// 全仓自洽：按钮引用的动作都存在，动作里的占位符都指得着东西。
    ///
    /// 这条是前面几条的兜底——以后再加一行，忘了写动作或写错控件 id，这里就红。
    /// 判据分两类：<c>{selection.*}</c> 必须指向真的声明过的通道，
    /// 其余必须是本面板里真的存在的控件 id。
    /// </summary>
    [Fact]
    public void EveryButtonResolvesAndEveryPlaceholderPointsAtSomethingReal()
    {
        var declared = Actions.GetProperty("actions").EnumerateArray()
            .Select(action => action.GetProperty("id").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        var widgets = PanelWidgets(OperationsPanel()).ToList();
        var controls = widgets
            .Where(w => w.GetProperty("kind").GetString() == "textbox")
            .Select(w => w.GetProperty("id").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        var rowFields = Page("overview").GetProperty("content").GetProperty("children")
            .EnumerateArray().Single(node => node.GetProperty("type").GetString() == "table")
            .GetProperty("columns").EnumerateArray()
            .Select(column => column.GetProperty("key").GetString()!)
            .Append("lifecycleAction")
            // LFS 文件表的行操作取被点那一行的 name（隐藏字段）与 path。
            .Concat(Node("projops", "lfs-files").GetProperty("columns").EnumerateArray()
                .Select(column => column.GetProperty("key").GetString()!))
            .Append("name")
            .ToHashSet(StringComparer.Ordinal);

        foreach (var rowAction in Node("projops", "lfs-files").GetProperty("rowActions").EnumerateArray())
        {
            var id = rowAction.GetProperty("action").GetString()!;
            Assert.True(declared.Contains(id), $"行操作绑了未声明的动作: {id}");
        }

        foreach (var button in widgets.Where(w => w.GetProperty("kind").GetString() == "button"))
        {
            var id = button.GetProperty("action").GetString()!;
            Assert.True(declared.Contains(id), $"按钮绑了未声明的动作: {id}");
        }

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

                    // {node} 由泳道组件在点击时提供，不是面板控件。
                    if (name == "node")
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
    public void OverviewUsesTheV56ControlPanelAndClickableColumns()
    {
        // V5.6：页签叫 Janus，顶栏是「刷新 + 搜索 + 年份」三件。
        Assert.Equal("Janus", Page("overview").GetProperty("title").GetString());
        var children = Page("overview").GetProperty("content").GetProperty("children")
            .EnumerateArray().ToList();
        Assert.DoesNotContain(children, node => node.GetProperty("type").GetString() == "text");

        var panel = children.Single(node => node.GetProperty("type").GetString() == "panel");
        var widgets = PanelWidgets(panel).ToList();
        Assert.Equal(3, widgets.Count);

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

        // 表格按两个通道取数：通道一变 Aurora 就重取，过滤不必自己写刷新。
        // refresh 不在参数里——它由「刷新」按钮显式立旗，见 janus.ui.refreshprojects。
        var args = children.Single(node => node.GetProperty("type").GetString() == "table")
            .GetProperty("dataSource").GetProperty("args");
        Assert.Equal("{selection.janus.overview.query.value}", args.GetProperty("query").GetString());
        Assert.Equal("{selection.janus.overview.year.value}", args.GetProperty("year").GetString());
        Assert.False(args.TryGetProperty("refresh", out _));

        var columns = children.Single(node => node.GetProperty("type").GetString() == "table")
            .GetProperty("columns").EnumerateArray().ToList();
        Assert.Equal(new[] { "项目", "z 级文件夹", "操作", "最近提交" },
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
        var dataSources = Description.GetProperty("pages").EnumerateArray()
            .SelectMany(page => Descend(page.GetProperty("content")))
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
        // 它们不会报错，只会被静默忽略——版面因此悄悄变样，而声明看上去毫无问题。
        var panels = Description.GetProperty("pages").EnumerateArray()
            .SelectMany(page => Descend(page.GetProperty("content")))
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
    /// 三块内容收进「项目操作」一页，由控制面板里的轮换选项框切换（Aurora REQ-UI-045/046）。
    ///
    /// 5.4.5 里它们是三个 <c>side=bottom</c> 的页面，由停靠层并成底部标签组——那是**三页**，
    /// 各占一条底边。收进一页之后版面只剩一条选项框，三块内容拿到同一块完整高度。
    ///
    /// **判据必须包含"选项框的候选项与分支的 case 逐字相等"**：两边对不上时不报任何错，
    /// 界面上的表现是切到某一项后下面永远停在第一支——与"这一支没数据"长得一样。
    /// </summary>
    [Fact]
    public void TheThreeSectionsAreFoldedIntoTheOperationsPageBehindOneOptionBox()
    {
        // 三页没了：整份描述里只剩总览、图谱、项目操作。
        Assert.Equal(
            new[] { "overview", "graph", "projops" },
            Description.GetProperty("pages").EnumerateArray()
                .Select(page => page.GetProperty("id").GetString())
                .ToArray());

        // 5.11.0：「Git 文件规则」拆成入库规则与 LFS 规则两页（DEC-032）。
        var titles = new[] { "入库规则", "LFS 规则", "分支历史", "GitHub" };

        var selector = PanelWidgets(OperationsPanel())
            .Single(w => w.TryGetProperty("id", out var id) && id.GetString() == "section");
        Assert.Equal("select", selector.GetProperty("mode").GetString());
        Assert.Equal(SectionChannel, selector.GetProperty("channel").GetString());
        Assert.Equal(
            titles,
            selector.GetProperty("options").EnumerateArray().Select(o => o.GetString()).ToArray());

        var container = Node("projops", "janus-sections");
        Assert.Equal("switch", container.GetProperty("type").GetString());
        Assert.Equal(
            "{selection." + SectionChannel + ".value}",
            container.GetProperty("source").GetString());
        Assert.Equal(
            titles,
            container.GetProperty("children").EnumerateArray()
                .Select(branch => branch.GetProperty("case").GetString())
                .ToArray());
    }

    /// <summary>
    /// 页面顶部那几段"这一页是干什么的"说明文字全部删掉。
    ///
    /// 它们占的是版面，给的是一次性的信息——每个打开这一页的人都要重新翻过去一次，
    /// 而其中的内容（清单全库共用、落地状态按选中项目、历史最多 200 条）
    /// 属于文档，不属于每次都要重新读一遍的界面。
    /// </summary>
    [Fact]
    public void NoPageStillCarriesAPreambleParagraph()
    {
        var preambles = Description.GetProperty("pages").EnumerateArray()
            .SelectMany(page => Descend(page.GetProperty("content")))
            .Where(node => node.TryGetProperty("type", out var type) && type.GetString() == "text")
            .Select(node => node.GetProperty("text").GetString() ?? "")
            .ToList();

        // 只留下短标题式的文字；说明段落一律不留。
        Assert.All(preambles, text => Assert.True(
            text.Length <= 12,
            $"页面里还留着一段说明文字：{text}"));
    }

    /// <summary>
    /// 切到哪一支就刷哪一支，不再留刷新按钮（5.9.0，REQ-017）。
    ///
    /// **为什么非有这一条不可**：Aurora 的 switch 在建页时一次建好三支，切走再切回来
    /// 用的是同一个控件实例（它刻意不重建，为的是保住滚动位置与选中行），因此切回来
    /// 不会重新取数。漏掉 commitAction 不报任何错——人看到的是一张不知道有多旧的表。
    ///
    /// 两个刷新按钮退役，连带 <c>janus.rules.refresh</c> / <c>janus.github.refresh</c>
    /// 两条动作声明：留着而没有按钮引用，下一个人会以为它们还在起作用。
    /// </summary>
    [Fact]
    public void EnteringASectionRefetchesItInsteadOfWaitingForARefreshButton()
    {
        var selector = PanelWidgets(OperationsPanel())
            .Single(w => w.TryGetProperty("id", out var id) && id.GetString() == "section");
        Assert.Equal("janus.section.enter", selector.GetProperty("commitAction").GetString());

        // 动作把选中的标题原样带过去，由 janus.ui.sectionenter 决定刷哪个节点。
        var enter = Action("janus.section.enter");
        Assert.Equal("janus.ui.sectionenter", enter.GetProperty("command").GetString());
        Assert.Equal("{section}", enter.GetProperty("args").GetProperty("section").GetString());

        // 项目操作页上只剩改名/新建/提交/推送四个按钮，没有任何「刷新 X」。
        var actions = Buttons("projops").Select(b => b.GetProperty("action").GetString()!).ToList();
        Assert.DoesNotContain("janus.rules.refresh", actions);
        Assert.DoesNotContain("janus.github.refresh", actions);

        var declared = Actions.GetProperty("actions").EnumerateArray()
            .Select(action => action.GetProperty("id").GetString()!)
            .ToList();
        Assert.DoesNotContain("janus.rules.refresh", declared);
        Assert.DoesNotContain("janus.github.refresh", declared);
    }

    /// <summary>
    /// 会跑 Git 的取数一律按**单个项目**取，项目名从选择通道来。
    ///
    /// 落地状态要对每个项目跑两条 <c>git ls-files</c>，全库一遍是 90 次进程启动；
    /// 分支历史与图谱同理。这些绝不能由「打开一个页签」触发。
    /// </summary>
    [Fact]
    public void EveryGitBackedViewIsScopedToTheSelectedProject()
    {
        foreach (var (node, view) in new[]
                 {
                     ("history-rows", "history"),
                     // LFS 两张表读的是**选中项目仓**的实况（5.11.0 从规则表里拆出来），
                     // 换项目要重取，而不是让人看着上一个项目的清单。
                     ("lfs-summary", "lfssummary"),
                     ("lfs-files", "lfs"),
                 })
        {
            var args = Node("projops", node).GetProperty("dataSource").GetProperty("args");
            Assert.Equal(view, args.GetProperty("view").GetString());
            Assert.Equal("{selection." + Channel + ".name}", args.GetProperty("name").GetString());
        }

        // 图谱同样跟着选中走。此前它固定画项目清单的第一条——不报错，只是一直不对。
        var graph = Page("graph").GetProperty("content").GetProperty("dataSource").GetProperty("args");
        Assert.Equal("{selection." + Channel + ".name}", graph.GetProperty("name").GetString());
    }

    /// <summary>
    /// 入库规则与 LFS 规则分成两页（5.11.0，DEC-032）。
    ///
    /// 入库规则是全库共用的清单，**不带项目名**——带了就会在每次换项目时白白重取一次。
    /// LFS 规则按选中项目取，且不到 100MB 的文件不在入库规则表里出现任何 LFS 字样：
    /// 两个问题挤在一张表里、靠「类型」一列区分，正是这次拆开的原因。
    /// </summary>
    [Fact]
    public void RulesAndLfsAreTwoSectionsWithTheirOwnScopes()
    {
        var rules = Node("projops", "rule-list").GetProperty("dataSource").GetProperty("args");
        Assert.Equal("excludes", rules.GetProperty("view").GetString());
        Assert.False(rules.TryGetProperty("name", out _), "入库规则与选中项目无关");

        var lfs = Descend(Node("projops", "janus-sections"))
            .Single(node => node.TryGetProperty("case", out var c) && c.GetString() == "LFS 规则");
        Assert.Equal(
            new[] { "lfs-summary", "lfs-files" },
            lfs.GetProperty("children").EnumerateArray().Select(n => n.GetProperty("id").GetString()).ToArray());

        var decisions = Node("projops", "lfs-files").GetProperty("rowActions").EnumerateArray()
            .Select(a => a.GetProperty("action").GetString()).ToArray();
        Assert.Equal(new[] { "janus.lfs.uselfs", "janus.lfs.untrack", "janus.lfs.clear" }, decisions);
        Assert.Equal("lfs", Action("janus.lfs.uselfs").GetProperty("args").GetProperty("decision").GetString());
        Assert.Equal("ignore", Action("janus.lfs.untrack").GetProperty("args").GetProperty("decision").GetString());
        Assert.Equal("none", Action("janus.lfs.clear").GetProperty("args").GetProperty("decision").GetString());
    }

    private static IEnumerable<JsonElement> Buttons(string pageId)
        => Descend(Page(pageId).GetProperty("content"))
            .Where(node => node.TryGetProperty("rows", out _))
            .SelectMany(PanelWidgets)
            .Where(widget => widget.GetProperty("kind").GetString() == "button");

    /// <summary>一个面板里的全部小组件，按行内顺序摊平。</summary>
    private static List<JsonElement> PanelWidgets(JsonElement panel)
        => panel.GetProperty("rows").EnumerateArray().SelectMany(Widgets).ToList();

    /// <summary>一行里的小组件。</summary>
    private static IEnumerable<JsonElement> Widgets(JsonElement row)
        => row.GetProperty("widgets").EnumerateArray();

    private static JsonElement Node(string pageId, string nodeId)
        => Descend(Page(pageId).GetProperty("content"))
            .Single(node => node.TryGetProperty("id", out var id) && id.GetString() == nodeId);

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
