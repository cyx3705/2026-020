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
    /// 四行「左标签 / 中控件 / 右按钮」：项目名+改名、新项目名+新建、提交描述+提交/推送，
    /// 外加 5.4.6 的子页面切换器。
    /// </summary>
    [Fact]
    public void TheOperationsPanelIsFourRowsOfLabelControlAndButtons()
    {
        var widgets = OperationsPanel().GetProperty("widgets").EnumerateArray().ToList();

        var textboxes = widgets
            .Where(w => w.GetProperty("kind").GetString() == "textbox")
            .Select(w => w.GetProperty("id").GetString())
            .ToList();

        // 切换器排在**最后**：它管的是自己下面那块内容。
        // 排在前面的话，人得隔着三行项目操作才把「选项框」和「下面变了」联系起来。
        Assert.Equal(new[] { "project-name", "new-project", "commit-message", "section" }, textboxes);

        // 每个按钮都跟前一个控件同行，因此前三个文本框正好切出三行。
        var buttons = widgets.Where(w => w.GetProperty("kind").GetString() == "button").ToList();
        Assert.Equal(4, buttons.Count);
        Assert.All(buttons, button => Assert.True(button.GetProperty("inline").GetBoolean()));

        // 第三行两个按钮：提交、推送。
        Assert.Equal(
            new[] { "改名", "新建", "提交当前项目", "推送当前项目" },
            buttons.Select(b => b.GetProperty("text").GetString()).ToArray());
    }

    /// <summary>
    /// 只有「项目名」跟随选中行。「新项目名」和「提交描述」是自由输入——
    /// 给它们加上 follows 的话，每换一次选中就把人正在打的字冲掉。
    /// </summary>
    [Fact]
    public void OnlyTheProjectNameBoxFollowsTheSelection()
    {
        var following = OperationsPanel().GetProperty("widgets").EnumerateArray()
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
        var buttons = OperationsPanel().GetProperty("widgets").EnumerateArray()
            .Where(w => w.GetProperty("kind").GetString() == "button")
            .ToList();

        Assert.All(buttons, button => Assert.Equal(
            Channel,
            button.GetProperty("enabledWhen").GetProperty("selected").GetString()));
    }

    /// <summary>
    /// 面板里不得出现 <c>required</c>。面板的必填校验是**全局**的——
    /// 任何一个必填框为空，面板上每个按钮都拒绝执行。
    /// 把「新项目名」标成必填，就会连带把「改名」和「提交」一起锁死，
    /// 而报出来的错说的是「新项目名为必填项」，看上去与改名毫无关系。
    /// </summary>
    [Fact]
    public void NoWidgetIsMarkedRequiredBecauseThatValidationIsPanelWide()
    {
        Assert.DoesNotContain(
            OperationsPanel().GetProperty("widgets").EnumerateArray(),
            widget => widget.TryGetProperty("required", out var required) && required.GetBoolean());
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
        Assert.Equal("janus.proj.commit", commit.GetProperty("command").GetString());
        Assert.Equal(
            "{selection." + Channel + ".name}",
            commit.GetProperty("args").GetProperty("name").GetString());
        Assert.Equal("{commit-message}", commit.GetProperty("args").GetProperty("msg").GetString());

        var push = Action("janus.project.push");
        Assert.Equal("janus.proj.push", push.GetProperty("command").GetString());
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

        var widgets = OperationsPanel().GetProperty("widgets").EnumerateArray().ToList();
        var controls = widgets
            .Where(w => w.GetProperty("kind").GetString() == "textbox")
            .Select(w => w.GetProperty("id").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

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

                    Assert.True(
                        controls.Contains(name),
                        $"动作 {action.GetProperty("id").GetString()} 引用了面板里没有的控件 {{{name}}}");
                }
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

        var titles = new[] { "Git 文件规则", "分支历史", "GitHub" };

        var selector = OperationsPanel().GetProperty("widgets").EnumerateArray()
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
    /// 两个刷新按钮必须落到 Aurora 的取数刷新台账，而不是把业务指令打到控制台。
    ///
    /// 后者是 5.4.4 之前这两个按钮的样子：指令确实跑了，界面上那张表一动不动——
    /// 「点了有反应」和「点了有用」在那一版里是两回事。
    /// </summary>
    [Fact]
    public void TheRefreshButtonsActuallyRefetchTheirOwnNodes()
    {
        // 5.4.6 起两条都**按节点**刷，不再按页：三块内容同处「项目操作」一页，
        // 按页刷会把没被点到的那两块一起带上，而 GitHub 那条要探 SSH 与凭据助手。
        var github = Action("janus.github.refresh");
        Assert.Equal("aurora.ui.refreshdata", github.GetProperty("command").GetString());
        Assert.Equal("github-rows", github.GetProperty("args").GetProperty("node").GetString());
        Assert.False(github.GetProperty("args").TryGetProperty("page", out _));

        // 规则那一支有两张表，而 refreshdata 一次只收一个节点，因此过一道自己的指令。
        var rules = Action("janus.rules.refresh");
        Assert.Equal("janus.ui.refreshrules", rules.GetProperty("command").GetString());

        // 两个按钮都还在「项目操作」页上，否则刷的是这里、按钮在别处。
        var actions = Buttons("projops").Select(b => b.GetProperty("action").GetString()).ToList();
        Assert.Contains("janus.rules.refresh", actions);
        Assert.Contains("janus.github.refresh", actions);
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
                     ("rule-state", "rulestate"),
                     ("history-rows", "history"),
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
    /// 排除清单那张表**不带项目名**：清单是全库共用的，读它只碰设置不碰 Git，
    /// 因此打开页签就该立刻出来，不必等人先选一个项目。
    /// </summary>
    [Fact]
    public void TheSharedExcludeListLoadsWithoutTouchingGit()
    {
        var args = Node("projops", "rule-list").GetProperty("dataSource").GetProperty("args");

        Assert.Equal("excludes", args.GetProperty("view").GetString());
        Assert.False(args.TryGetProperty("name", out _));
    }

    private static IEnumerable<JsonElement> Buttons(string pageId)
        => Descend(Page(pageId).GetProperty("content"))
            .Where(node => node.TryGetProperty("widgets", out _))
            .SelectMany(node => node.GetProperty("widgets").EnumerateArray())
            .Where(widget => widget.GetProperty("kind").GetString() == "button");

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
