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

    /// <summary>
    /// LFS 面板的一格「标签 | 值」：只返回一个候选的选择框。
    /// 取数引用选中项目与子页面两个通道——引用哪个，哪个一变就重取。
    /// </summary>
    private static object LfsStat(string id, string label, string item) => new
    {
        kind = "textbox",
        id,
        label,
        mode = "select",
        optionsSource = new
        {
            command = "janus.ui.data",
            args = new
            {
                view = "lfsstat",
                item,
                name = SelectedProject,
                section = "{selection." + SectionChannel + ".value}",
            },
        },
    };

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
                scene = "HistoryJanus",
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
                                // 点项目名打开项目目录。Aurora 表格没有双击事件，单元格动作是唯一入口。
                                new { key = "name", title = "项目", width = "220", cellAction = "janus.project.open" },
                                new { key = "zFolders", title = "z 级文件夹", width = "110", cellAction = "janus.project.openmeta" },
                                // 单元格写「动作 + 状态符号」；点它执行的仍是 lifecycleAction 那一格。
                                new { key = "status", title = "操作", width = "120", cellAction = "janus.project.action" },
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
                scene = "HistoryJanus",
                placement = new { side = "bottom", ratio = 0.33, visible = true, singleton = true },
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
                scene = "HistoryJanus",
                placement = new { side = "left", ratio = 0.36, visible = true, singleton = true },
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
                                //
                                // commitAction（5.9.0）：切到哪一支就刷哪一支。
                                // switch 的三支在建页时一次建好、切走再切回来是**同一个控件实例**
                                // （Aurora BuildSwitch 明写不重建，为的是保住滚动位置与选中行），
                                // 因此切回来不会重新取数——旧版靠「刷新规则 / 刷新 GitHub」两个按钮
                                // 补这一手，等于把刷新的责任推给人。
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
                                            options = new[] { SectionRules, SectionLfs, SectionHistory, SectionGitHub },
                                            commitAction = "janus.section.enter",
                                        },
                                    },
                                },
                            },
                        },
                        // 四块子内容。case 与上面选项框的候选项是同一组常量，
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
                                // 入库规则：全库共用的清单，什么进仓库、什么不进。
                                // 目录与扩展名同表，靠末尾的 / 与开头的 * 自己区分。
                                // 与选中项目无关，所以取数参数里没有项目名。
                                //
                                // 没有「刷新规则」按钮：切到这一支就重取（见 section 那一行的 commitAction）。
                                new
                                {
                                    type = "table",
                                    @case = SectionRules,
                                    id = "rule-list",
                                    dataSource = new
                                    {
                                        command = "janus.ui.data",
                                        args = new { view = "excludes" },
                                    },
                                    columns = new object[]
                                    {
                                        new { key = "kind", title = "类型", width = "70" },
                                        new { key = "rule", title = "规则", width = "*" },
                                    },
                                },
                                // LFS 规则（5.12.0）：按选中项目。上面一块控制面板，两行「标签 | 值」；
                                // 下面一张表只列 ≥100MB 的文件，三列：文件、大小、操作。
                                //
                                // 面板里的值用只返回一个候选的选择框显示——Aurora 面板没有取数文字控件
                                // （用户选定这样凑，不为此改 Aurora）。选择框只在它引用的通道变化时重取，
                                // 所以取数参数同时引用选中项目与子页面两个通道：换项目、切回本页都会刷新。
                                // 点「操作」改决定后表格立刻重取，面板的决定计数要等下一次换项目或切页。
                                new
                                {
                                    type = "stack",
                                    @case = SectionLfs,
                                    gap = "normal",
                                    children = new object[]
                                    {
                                        new
                                        {
                                            type = "panel",
                                            id = "janus-lfs",
                                            rows = new object[]
                                            {
                                                new
                                                {
                                                    mode = "even",
                                                    widgets = new object[]
                                                    {
                                                        LfsStat("lfs-compliance", "合规", "compliance"),
                                                        LfsStat("lfs-pointers", "LFS 指针", "pointers"),
                                                        LfsStat("lfs-bytes", "大小", "bytes"),
                                                    },
                                                },
                                                new
                                                {
                                                    mode = "even",
                                                    widgets = new object[]
                                                    {
                                                        LfsStat("lfs-oversize", "≥100MB", "oversize"),
                                                        LfsStat("lfs-decided-lfs", "LFS", "lfs"),
                                                        LfsStat("lfs-decided-ignore", "不纳入", "ignore"),
                                                        LfsStat("lfs-undecided", "未决定", "undecided"),
                                                    },
                                                },
                                            },
                                        },
                                        // 「操作」格与总览一样是多态按钮：显示当前决定加符号，
                                        // 点一下切到另一个去向（未决定 → LFS 指针 ↔ 不纳入 git）。
                                        new
                                        {
                                            type = "table",
                                            id = "lfs-files",
                                            dataSource = new
                                            {
                                                command = "janus.ui.data",
                                                args = new { view = "lfs", name = SelectedProject },
                                            },
                                            columns = new object[]
                                            {
                                                new { key = "path", title = "文件", width = "*" },
                                                new { key = "size", title = "大小", width = "90" },
                                                new { key = "op", title = "操作", width = "110", cellAction = "janus.lfs.toggle" },
                                            },
                                            view = new { filterable = true, sortable = true, selection = "single" },
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
                                // 同样没有「刷新 GitHub」按钮：切到这一支就重探。
                                // 这一支贵（要探 SSH 与凭据助手），正因为贵才不该在人看着
                                // 另一支的时候白跑，也不该靠人记得回来点一下刷新。
                                new
                                {
                                    type = "table",
                                    @case = SectionGitHub,
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
                id = "janus.project.open",
                title = "打开项目目录",
                command = "janus.proj.open",
                args = new Dictionary<string, string> { ["name"] = "{name}" },
                summary = "在资源管理器中打开项目目录",
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
                summary = "按重新校验后的项目状态执行提交、推送、同步、归档、拉取或刷新",
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
            // LFS 规则表「操作」格（5.12.0）。{name}/{path}/{next} 取被点那一行：
            // next 是投影按当前决定算好的下一个去向，页面不自己判断。
            new
            {
                id = "janus.lfs.toggle",
                title = "切换 LFS 去向",
                command = "janus.ui.lfsdecide",
                args = new Dictionary<string, string>
                {
                    ["name"] = "{name}",
                    ["path"] = "{path}",
                    ["decision"] = "{next}",
                },
                summary = "在 LFS 指针与不纳入 git 之间切换；下次提交生效，历史不变",
            },
            // 切到哪一支就刷哪一支（5.9.0）。它不是按钮，是子页面选项框的
            // commitAction——选项框一变就带着新标题打过来。
            //
            // 刷新落到 Aurora 的取数刷新台账，而不是把指令打到控制台（那样表格一动不动），
            // 并且**按节点**刷：一页上挂着三支，刷整页会把没人看的那两支也跑一遍，
            // 而 GitHub 那一支要探 SSH 与凭据助手。
            new
            {
                id = "janus.section.enter",
                title = "切换子页面",
                command = "janus.ui.sectionenter",
                args = new Dictionary<string, string> { ["section"] = "{section}" },
                summary = "切到某个子页面时重取它的数据",
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
