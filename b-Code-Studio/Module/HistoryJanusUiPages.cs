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
    /// 弹窗子页（5.16.0）：入库规则、LFS 规则、GitHub 不再挤在项目操作页的切换容器里，
    /// 各自是一张平时不露面的页，按钮把它浮成置顶小窗（<c>aurora.ui.float</c>）。
    /// 页 id 是全局窗口名，所以带 janus- 前缀。
    /// </summary>
    internal const string RulesPage = "janus-rules";

    internal const string LfsPage = "janus-lfs";

    internal const string GitHubPage = "janus-github";

    /// <summary>弹窗子页的初次落位：不露面，靠浮出打开。</summary>
    private static readonly object PopupPlacement = new { side = "right", ratio = 0.3, visible = false, singleton = true };

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
                // 5.16.0：项目表竖着停在左侧，中间让给项目操作页。
                placement = new { side = "left", ratio = 0.3, visible = true, singleton = true },
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
                            // 两行（5.16.0，左侧变窄后一行放不下四件）：刷新 + 搜索；年份 + 排列。
                            // 搜索、年份、排列都只作用在**已经取到的那份清单**上（见 LoadProjectsAsync）——
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
                                    },
                                },
                                new
                                {
                                    mode = "even",
                                    widgets = new object[]
                                    {
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
                                        // 「排列」两态（5.15.3）：第一态编号是原有行为，第二态按操作分组。
                                        // 同样只在已取到的清单上重排，不碰 Git。
                                        new
                                        {
                                            kind = "textbox",
                                            id = "overview-sort",
                                            label = "排列",
                                            mode = "select",
                                            minWidth = 72,
                                            channel = OverviewSortChannel,
                                            options = new[] { SortByNumber, SortByAction },
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
                                    sort = "{selection." + OverviewSortChannel + ".value}",
                                },
                            },
                            // 「最近提交」列删了（5.16.0）：竖栏里放不下，选中后中间页的分支表与历史表都有。
                            columns = new object[]
                            {
                                // 点项目名打开项目目录。Aurora 表格没有双击事件，单元格动作是唯一入口。
                                new { key = "name", title = "项目", width = "220", cellAction = "janus.project.open" },
                                new { key = "zFolders", title = "z 级文件夹", width = "110", cellAction = "janus.project.openmeta" },
                                // 单元格写「动作 + 状态符号」；点它执行的仍是 lifecycleAction 那一格。
                                new { key = "status", title = "操作", width = "120", cellAction = "janus.project.action" },
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
                // 5.16.0：项目操作是主页面，占中间。
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
                            id = "janus-projops",
                            text = "项目操作",
                            // 两行（5.16.0）。提交描述行删了：「提交」先摆差异再弹窗要描述，
                            // 一个常驻的描述框只会让人以为不填就提交不了。
                            //
                            // 文本框都**不写必填**：Aurora 的 required 已在 V3 退役——
                            // 它是全局的，一个空框会把面板上每个按钮一起锁死。
                            rows = new object[]
                            {
                                // 第一行：项目名 + 改名，新项目名 + 新建。一行两个 flex 只认第一个，
                                // 所以「项目名」吃余量，「新项目名」给一个够用的最窄宽度。
                                new
                                {
                                    mode = "flex",
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
                                        new
                                        {
                                            kind = "textbox",
                                            id = "new-project",
                                            label = "新项目名",
                                            minWidth = 200,
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
                                // 第二行：提交、推送，以及三个弹窗子页，均布。
                                // 分支历史不在这里：它是这一页下半截的常驻内容。
                                new
                                {
                                    mode = "even",
                                    widgets = new object[]
                                    {
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
                                        new
                                        {
                                            kind = "button",
                                            action = "janus.section.rules",
                                            text = SectionRules,
                                            enabledWhen = new { selected = ProjectChannel },
                                        },
                                        new
                                        {
                                            kind = "button",
                                            action = "janus.section.lfs",
                                            text = SectionLfs,
                                            enabledWhen = new { selected = ProjectChannel },
                                        },
                                        new
                                        {
                                            kind = "button",
                                            action = "janus.section.github",
                                            text = SectionGitHub,
                                            enabledWhen = new { selected = ProjectChannel },
                                        },
                                    },
                                },
                            },
                        },
                        // 分支控制（5.16.0）：选中项目的本地分支，当前分支在最上。
                        // 点分支名 → 下面的历史表换成那条分支；右键 → 切换 / 合并到当前 / 从此分出 / 删除。
                        // 四条都只进右键菜单（inline=false）：每行挂四个按钮会把分支名挤成一条缝。
                        new
                        {
                            type = "table",
                            id = "branch-rows",
                            dataSource = new
                            {
                                command = "janus.ui.data",
                                args = new { view = "branches", name = SelectedProject },
                            },
                            columns = new object[]
                            {
                                new { key = "branch", title = "分支", width = "160", cellAction = "janus.branch.pick" },
                                new { key = "state", title = "状态", width = "100" },
                                new { key = "track", title = "上游", width = "90" },
                                new { key = "time", title = "最近提交", width = "130" },
                                new { key = "subject", title = "说明", width = "*" },
                            },
                            rowActions = new object[]
                            {
                                new { action = "janus.branch.switch", title = "切换到此分支", inline = false },
                                new { action = "janus.branch.merge", title = "合并到当前分支", inline = false },
                                new { action = "janus.branch.create", title = "从此分出新分支", inline = false },
                                new { action = "janus.branch.delete", title = "删除分支", style = "danger", inline = false },
                            },
                            view = new { sortable = false, selection = "single" },
                        },
                        new
                        {
                            type = "table",
                            id = "history-rows",
                            // 哪条分支由模块记着（点分支名时改），默认是当前分支；
                            // 不走通道：表格不会自动选中首行，按通道取的话没点过分支就是一张空表。
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
                    },
                },
            },
            // 入库规则：全库共用的清单，什么进仓库、什么不进。
            // 目录与扩展名同表，靠末尾的 / 与开头的 * 自己区分。与选中项目无关，取数参数里没有项目名。
            new
            {
                id = RulesPage,
                title = SectionRules,
                scene = "HistoryJanus",
                placement = PopupPlacement,
                content = new
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
                        new { key = "kind", title = "类型", width = "70" },
                        new { key = "rule", title = "规则", width = "*" },
                    },
                },
            },
            // LFS 规则：按选中项目。上面一行汇总（合规只给 ✓ / ✗），下面列出全部 ≥100MB 的文件。
            // 5.16.0 起汇总是一行三列的表，不再是三个只有一个候选的选择框：
            // 表格能被 refreshdata 按节点重取，打开弹窗时就能拿到新的一次检查。
            new
            {
                id = LfsPage,
                title = SectionLfs,
                scene = "HistoryJanus",
                placement = PopupPlacement,
                content = new
                {
                    type = "stack",
                    gap = "normal",
                    children = new object[]
                    {
                        new
                        {
                            type = "table",
                            id = "lfs-stat",
                            dataSource = new
                            {
                                command = "janus.ui.data",
                                args = new { view = "lfsstat", name = SelectedProject },
                            },
                            columns = new object[]
                            {
                                new { key = "compliance", title = "合规", width = "60" },
                                new { key = "bytes", title = "指针大小", width = "100" },
                                new { key = "oversize", title = "≥100MB", width = "80" },
                            },
                        },
                        // 「操作」格与总览一样是多态按钮（5.12.1）：未决显示「未决」，点了弹窗二选一；
                        // 未纳入显示「纳入」，点了走 LFS 指针；已纳入显示「取消纳入」，点了取消 LFS 指针。
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
            },
            // GitHub：远端地址就地编辑。这一张要探 SSH 与凭据助手，打开弹窗时才取。
            new
            {
                id = GitHubPage,
                title = SectionGitHub,
                scene = "HistoryJanus",
                placement = PopupPlacement,
                content = new
                {
                    type = "table",
                    id = "github-rows",
                    dataSource = new
                    {
                        command = "janus.ui.data",
                        args = new { view = "github", name = SelectedProject },
                    },
                    columns = new object[]
                    {
                        new { key = "item", title = "项", width = "120" },
                        new { key = "value", title = "值", width = "*",
                            cellEditAction = "janus.github.setremote", editableWhen = "editable" },
                    },
                },
            },
        },
    }, new JsonSerializerOptions { WriteIndented = false });

    /// <summary>分支表右键菜单的四条动作：参数都取被点那一行的项目与分支，具体怎么做由 janus.ui.branchop 决定。</summary>
    private static object BranchAction(string op, string title, string summary, bool danger = false) => new
    {
        id = "janus.branch." + op,
        title,
        command = "janus.ui.branchop",
        args = new Dictionary<string, string>
        {
            ["name"] = "{project}",
            ["branch"] = "{branch}",
            ["op"] = op,
        },
        danger,
        summary,
    };

    /// <summary>弹窗子页按钮：浮出那一页并重取它的表。</summary>
    private static object SectionAction(string id, string section) => new
    {
        id,
        title = section,
        command = "janus.ui.opensection",
        args = new Dictionary<string, string> { ["section"] = section },
        summary = $"弹出「{section}」窗口",
    };

    private static readonly string ActionsJson = JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        owner = Owner,
        actions = new object[]
        {
            new
            {
                id = "janus.github.setremote",
                title = "保存远端",
                command = "janus.ui.setremote",
                args = new Dictionary<string, string>
                {
                    ["name"] = "{name}",
                    ["url"] = "{value}",
                },
                summary = "将选中项目的 origin 拉取与推送地址设为输入的仓库地址",
            },
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
            // 提交不带描述（5.16.0 删了描述框）：先弹差异，选「提交」后再弹窗要描述。
            new
            {
                id = "janus.project.commit",
                title = "提交当前项目",
                command = "janus.ui.projectaction",
                args = new Dictionary<string, string>
                {
                    ["action"] = "提交",
                    ["name"] = SelectedProject,
                },
                summary = "先看本次差异，再填描述提交到本地仓库",
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
            SectionAction("janus.section.rules", SectionRules),
            SectionAction("janus.section.lfs", SectionLfs),
            SectionAction("janus.section.github", SectionGitHub),
            // 点分支名：下面的历史表换成这条分支（不切换工作树）。
            new
            {
                id = "janus.branch.pick",
                title = "查看分支历史",
                command = "janus.ui.branchpick",
                args = new Dictionary<string, string>
                {
                    ["name"] = "{project}",
                    ["branch"] = "{branch}",
                },
                summary = "在下面的表格里显示这条分支的提交历史",
            },
            BranchAction(BranchOpSwitch, "切换到此分支", "把项目工作树切换到这条分支；工作树有未提交内容时先弹提交窗口"),
            BranchAction(BranchOpMerge, "合并到当前分支", "把这条分支合并进当前分支；有冲突时什么都不动"),
            BranchAction(BranchOpCreate, "从此分出新分支", "以这条分支为起点新建一条分支"),
            BranchAction(BranchOpDelete, "删除分支", "删除这条本地分支；当前分支与 main 不能删", danger: true),
            // LFS 规则表「操作」格（5.12.0；5.12.1 改文字与去向）。{name}/{path}/{next} 取被点那一行：
            // next 是投影按当前决定算好的下一步，页面不自己判断（choose = 弹窗让人定）。
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
                summary = "未决的弹窗定去向；未纳入的纳入 LFS 指针；已纳入的取消纳入。下次提交生效，历史不变",
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
