using System.Text.Json;

namespace HistoryJanus.Module;

/// <summary>
/// Janus 的**页面描述与动作声明**。两份 JSON 常量加起来占这个类型一半篇幅，
/// 而它们与取数、投影是两件事：这里回答「页面长什么样、有哪些动作」，
/// <see cref="HistoryJanusUiModule"/> 那一半回答「数据从哪来、怎么摊成行」。
///
/// 分开还有一条现实理由：改一次列宽会让整份取数代码进同一个差分，
/// 评审时看不出哪几行是真正的行为改动。
/// </summary>
internal static partial class HistoryJanusUiCommands
{
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
                // 页签就叫 Janus：这一页是这个模块的门面，页签上再写「项目总览」
                // 只是把「哪个模块」这条信息让给了一句功能描述。
                title = "Janus",
                placement = new { side = "center", visible = true, singleton = true },
                content = new
                {
                    type = "stack",
                    gap = "normal",
                    children = new object[]
                    {
                        new
                        {
                            type = "panel",
                            id = "janus-overview-controls",
                            // 一行三件：刷新、搜索、年份。搜索框吃掉余量，另外两个各自最窄。
                            // 搜索与年份都只作用在**已经取到的那份清单**上（见 LoadProjectsAsync）——
                            // 敲一个字符就 fetch 一轮远端是不能接受的。
                            rows = new object[]
                            {
                                new
                                {
                                    mode = "flex",
                                    widgets = new object[]
                                    {
                                        new
                                        {
                                            kind = "button",
                                            action = "janus.projects.refresh",
                                            text = "刷新",
                                            icon = "refresh-cw",
                                        },
                                        new
                                        {
                                            kind = "textbox",
                                            id = "overview-query",
                                            label = "搜索",
                                            flex = true,
                                            channel = OverviewQueryChannel,
                                        },
                                        new
                                        {
                                            kind = "textbox",
                                            id = "overview-year",
                                            label = "年份",
                                            mode = "select",
                                            minWidth = 96,
                                            channel = OverviewYearChannel,
                                            // 候选跟着实际项目走：新登记一个 2027 的项目，
                                            // 下拉里就多一个 2027，不必回来改这份描述。
                                            optionsSource = new
                                            {
                                                command = "janus.ui.data",
                                                args = new { view = "years" },
                                            },
                                        },
                                    },
                                },
                            },
                        },
                        new
                        {
                            type = "table",
                            id = "projects",
                            // 选中行发上界面级通道；页内节点 id 到不了另一页，通道名可以。
                            channel = ProjectChannel,
                            // args 必须是字符串映射；JSON 布尔值会让 Aurora 拒绝整份 Janus 页面描述。
                            dataSource = new
                            {
                                command = "janus.ui.data",
                                rowKey = "name",
                                args = new
                                {
                                    view = "projects",
                                    query = "{selection." + OverviewQueryChannel + ".value}",
                                    year = "{selection." + OverviewYearChannel + ".value}",
                                },
                            },
                            columns = new object[]
                            {
                                new { key = "name", title = "项目", width = "220" },
                                new { key = "zFolders", title = "z 级文件夹", width = "110", cellAction = "janus.project.openmeta" },
                                new { key = "status", title = "状态 / 动作", width = "120", cellAction = "janus.project.action" },
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
                            // 四行，行是**声明出来的**（Aurora 面板协议第三版），
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
                                            text = "提交",
                                            enabledWhen = new { selected = ProjectChannel },
                                        },
                                        new
                                        {
                                            kind = "button",
                                            action = "janus.project.push",
                                            text = "推送",
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
                                    // 不列「提交」与「作者」。短 sha 在这一页上
                                    // 没有可点的去处（要看某一次提交走分支图谱），
                                    // 而作者在单人仓里每行都一样——两列合起来吃掉 180px，
                                    // 让真正要读的「说明」被挤成一条缝。
                                    columns = new object[]
                                    {
                                        new { key = "time", title = "时间", width = "150" },
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
                id = "janus.projects.refresh",
                title = "刷新",
                command = "janus.ui.refreshprojects",
                summary = "查询远端并刷新项目总览",
            },
            new
            {
                id = "janus.project.openmeta",
                title = "打开 z 级文件夹",
                command = "janus.ui.openmeta",
                args = new Dictionary<string, string> { ["name"] = "{name}" },
                summary = "打开项目直属 z/Z 文件夹",
            },
            new
            {
                id = "janus.project.action",
                title = "项目状态操作",
                command = "janus.ui.projectaction",
                args = new Dictionary<string, string>
                {
                    ["name"] = "{name}",
                    ["action"] = "{lifecycleAction}",
                },
                summary = "按重新校验后的项目状态执行提交、推送、同步、归档或拉取",
            },
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
                command = "janus.ui.projectaction",
                args = new Dictionary<string, string>
                {
                    ["action"] = "提交",
                    ["name"] = SelectedProject,
                    ["msg"] = "{commit-message}",
                },
                summary = "把选中项目按「提交描述」提交到本地仓库",
            },
            new
            {
                id = "janus.project.push",
                title = "推送当前项目",
                command = "janus.ui.projectaction",
                args = new Dictionary<string, string>
                {
                    ["action"] = "推送",
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
