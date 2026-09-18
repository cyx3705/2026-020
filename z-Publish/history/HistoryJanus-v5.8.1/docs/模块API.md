# HistoryJanus 5.8.1 模块 API

本文件是其他模块和项目消费 HistoryJanus 的唯一人工合同。运行时命令目录是参数、确认策略和可用性的最终真值；历史文档和 Janus 内部类型不构成公开 API。

## 正式消费入口

- 正式快照：`z-Publish/HistoryJanus-v5.7.0/`。
- 模块名：`HistoryJanus`。
- 版本：`5.8.1`。
- 入口：`HistoryJanus.dll`。
- 宿主基线：HistoryVulcan `5.1.2` current-host 快照，从 `2026-023-HistoryVulcan/z-Publish` 消费；该快照的 `sourceDirty` 仍由 HistoryVulcan manifest 如实标记。
- 主题：页面使用 `Aurora.Brush.*` 动态资源，跟随宿主深色/浅色切换，不在模块内维护第二套主题。
- 弹窗：模块不得 `new Window`。提交预览、差异预览、恢复提交说明、GitHub 通知与诊断走 `aurora.ui.dialog`（`message` / `prompt` / `content`）。写操作警告仍走命令 `ConfirmPrompt`，由宿主确认策略弹出，页面不得再叠一层。
- 命令来源：`module:HistoryJanus`。
- 命令命名：`janus.<类>.<方法>` 三段式全小写（详见 `b-Office/current/指令优化规范.md`）。
- UI：启用。
- MCP：只读投影。

本文件描述活动源的 `5.8.1` 候选合同；只有用户另行授权经宿主 `vulcan.dev.submit` / `finish` 正式发布后，同版本 manifest 和二进制才会提升到
`z-Publish/HistoryJanus-vX.Y.Z/`。发布前，z 快照自身的 manifest 与 checksum 仍是正式运行版本的真值。
其他项目从 `z-Publish/HistoryJanus-vX.Y.Z/docs/` 或 `diana.docs.read domain=janus` 读取已发布 API，从该版本化快照读取 `module.manifest.json`、二进制和
`SHA256SUMS`；不要从 Janus 的 `bin/obj`、HistoryVulcan 工作树或 Janus 历史文档建立依赖。Diana 不发布。

## 宿主接入

Janus 仅实现 `IModuleContextAware`。HistoryVulcan 5.1.2 注入 `IModuleContext` 后，Janus 通过 `context.RegisterCommands` 使用宿主 `CommandRegistry` 与 `CommandBus` 注册业务命令和描述化前端投影；页面不再由模块注册 WPF 窗口。消费者模块通过自己的宿主上下文取得同一个 `CommandBus`，按命令名调用 Janus；不得构造 Janus 服务、引用内部 DTO，或自行加载 Janus DLL。

```csharp
var result = await context.Bus.ExecuteAsync("janus.proj.list", "filter=2026");
if (!result.Success)
    throw new InvalidOperationException(result.Message);
```

结构化结果跨宿主 HTTP 边界时可能表现为 `JsonElement`。消费者应按运行时目录说明投影为自己的 DTO，不依赖 Janus 页面内部模型。

## 5.7.0 前端更新

表格与面板的提交/推送统一调用 `janus.ui.projectaction`；提交可传 `msg`，省略时弹窗输入。
操作成功后先重读该项目的本地状态与远端引用，替换缓存对应行，再等待总览刷新完成；不触发全库 fetch。
推送后按真实状态进入下一步（通常为同步），失败或取消不直接推进状态。项目表以 `name` 为稳定行键。

需 Aurora ≥ 1.21.0 完成整套体验。项目表合并状态与动作列，点击状态文字仍调用
`janus.project.action`，提交/推送/同步/归档/拉取的判断、确认及业务命令不变。
`statusSymbol` 数据字段保留兼容，页面不再单独显示该列。
Aurora 统一提供按钮运行进度与防重复触发、图谱无滚动条的拖拽平移、规则表有界可滚动视口；
Janus 不自建按钮或滚动控件，不复制前端状态机。

## 描述化页面（Aurora V1）

| ID | 标题 | 默认位置 | 用途 |
| --- | --- | --- | --- |
| `overview` | 项目总览 | 中央工作区 | “刷新”控制面板及“项目 / z 级文件夹 / 状态与动作 / 最近提交”四列表格；状态文字直接绑定原生命周期动作 |
| `graph` | 分支图谱 | 并入控制台标签组 | 页面描述中的 `swimlane` 提交 DAG；以 `tabTarget=console` 与宿主控制台同组 |
| `projops` | 项目操作 | 左侧（宽度占比 `0.38`） | 一块四行控制面板（项目名/改名、新项目名/新建、提交描述/提交/推送、子页面切换），下接一个 `switch` 容器 |

`projops` 里的 `switch` 容器 `janus-sections` 收下三块子内容，由面板第四行的轮换选项框
经 `janus.section` 通道切换（Aurora REQ-UI-045/046）。**它们不是页面，也没有页面 ID**：

| `case` | 内容 |
| --- | --- |
| `Git 文件规则` | 「刷新规则」按钮，加全库共用的排除清单与**当前选中项目**的托管块落地状态两张表 |
| `分支历史` | **当前选中项目**从分叉点到 HEAD 的自有提交（最多 200 条） |
| `GitHub` | 「刷新 GitHub」按钮，加 Git、GCM、提交身份、origin 与 SSH 的当前状态（项/值两列） |

三个 ID 是布局兼容合同。**默认落位**：`overview` 占中央工作区且不声明 `DefaultTabTarget`；`graph` 以 `Tab` 并入宿主 `console`（控制台由 HistoryVulcan 注册，不依赖 Mercury）；`projops` 停靠左侧、默认宽度占比 0.38。已保存的用户布局优先于默认落位。其他模块不能重复注册这些 ID；需要联动项目选择时应通过 Janus 命令读取事实，不访问页面私有状态。

**页面 ID 只有上表三个。** `tree`、`meta`、`history`、`rules`、`github` 都已撤销——
其中 `rules` / `history` / `github` 在 5.4.6 收进 `projops` 的 `switch` 容器（REQ-015，需 Aurora ≥ 1.8.17）。
还按这三个页面 ID 建立布局依赖的消费方，**它们不再存在**。

GitHub 写操作（登录、注销、提交身份、origin 修改）由页面通过 `janus.github.*` 命令总线执行并服从宿主确认策略；只读刷新和连接诊断可使用模块内连接服务。

## 命令目录

旧名（`proj.*` / `git.rule.*` / `github.*` / `debug.*` / `HistoryJanus.Status`）自 3.5.0 起一次作废、不留别名，
完整映射见 `b-Office/current/指令优化规范.md`。`debug` 类已整体退役，Janus 不公开诊断命令。

当前：业务命令按下表，加模块宿主投影的 `janus.status`，**运行时命令总数 48 条**。
写入命令不进入 MCP 只读投影。逐版本的条数演变见 `b-Office/history/5.6.0-模块API历史备注.md`。

### 模块与读取

| 命令 | 模式 | 用途 |
| --- | --- | --- |
| `janus.status` | 只读 | 返回模块身份和注册状态 |
| `janus.proj.list` | 只读 | 列出已登记项目仓；`status=true` 时结果含 `IsClean`（`true/false/null`）和 `WorktreeStatusMessage`，默认不扫描状态 |
| `janus.proj.tree` | 只读 | 读取或刷新继承树 |
| `janus.proj.scan` | 只读 | 扫描项目大文件 |
| `janus.proj.config` | 只读 | 返回项目命令配置 |
| `janus.proj.metas` | 只读 | 列出项目 z/Z 级元文件夹 |
| `janus.proj.refresh` | 本机写入 | fetch 全部项目的 origin/main 并返回生命周期状态 |
| `janus.proj.sync` | 本机写入 | 仅快进同步并记录已验证 SHA；分叉时拒绝 merge/rebase |
| `janus.proj.archive` | 确认写入 | 复核干净且两端一致后只保留直属 z/Z 文件夹 |
| `janus.proj.pull` | 本机写入 | 从归档记录克隆远端并以本地 z/Z 内容覆盖恢复 |
| `janus.history.list` | 只读 | 列出分支自有提交 |
| `janus.history.show` | 只读 | 读取提交详情 |
| `janus.history.diff` | 只读 | 预览历史节点与 HEAD 的差异 |
| `janus.gitrule.list` | 只读 | 查看全库共用的不纳入仓库清单，及各项目托管块落地情况 |
| `janus.github.status` | 只读 | 服务器 Git、GCM、提交身份、origin 和 SSH 状态 |
| `janus.github.accounts` | 只读 | 列出 GCM 中已知的 GitHub HTTPS 凭据账号 |
| `janus.github.test` | 只读 | 检测 GitHub SSH/HTTPS 连接（`transport=auto\|ssh\|https`，`timeout=1..120`），不执行 push |
| `janus.graph.summary` | 只读 | 查看编号项目图谱摘要：HEAD、节点数、分支与工作树 dirty 状态 |
| `janus.graph.branches` | 只读 | 列出该项目仓的主线 `main` 与平行分支（含已合并历史）及基线关系 |
| `janus.graph.commits` | 只读 | 按时间序分页读取本仓提交节点与父边（含 merge parent） |
| `janus.graph.node` | 只读 | 读取单个提交的父边、文件差异摘要；证据槽本阶段为空 |
| `janus.ui.describe` | 只读 | 返回 Aurora 页面描述协议 V1 |
| `janus.ui.actions` | 只读 | 返回 Aurora 动作声明协议 V1 |
| `janus.ui.data` | 只读 | 按 `view` 返回页面数据；`projects` 保留 `name`、`isClean`、`subject` 并增加 `zFolders`、`status`、`statusSymbol`、`lifecycleAction`、`archived`、`lifecycleState`。单个 z/Z 文件夹的 `zFolders` 为真实名称；`status` 为「动作 + 空格 + 符号」，符号固定映射提交 `●`、推送 `↑`、同步 `↔`、归档 `□`、拉取 `↓`、刷新 `?`（远端未确认或尚未查询时动作为「刷新」）。首次由界面（来源 `UI`）取 `projects` 时先返回不查远端的清单，再在后台 fetch 全部项目并重取表格。`excludes` 返回单张规则表：LFS（单文件超过 100MB）、`z-*` 入库例外、逐条不入库目录与扩展名。`graph` / `history` 缺 `name` 时不取数 |
| `janus.ui.graphnode` | 只读 | 将描述化图节点 ID 投影为提交详情 |
| `janus.ui.refreshrules` | 只读 | 「刷新规则」的落点：按节点重取 `rule-list`（5.8.1 起规则只剩这一张表）；不按页刷，以免带上分支历史与 GitHub |
| `janus.ui.refreshprojects` | 本机 UI | 重新查询远端并刷新项目表 |
| `janus.ui.projectaction` | 本机 UI | 重新校验行状态后执行提交、推送、同步、归档或拉取；`action=刷新` 只带 fetch 复核该项目并回写这一行，仍连不上时返回失败 |
| `janus.ui.openmeta` | 本机 UI | 0 个 z/Z 明确提示，1 个直接打开，多个经 choice 弹窗选择 |
| `janus.github.login` | 确认写入 | 启动服务器本机 Git Credential Manager 登录流程 |
| `janus.github.logout` | 确认写入 | 注销指定 GitHub HTTPS 凭据账号 |
| `janus.github.identity` | 预览/确认写入 | 预览或修改 repository/global Git 提交身份；`apply=true` 触发确认 |
| `janus.github.remote` | 预览/确认写入 | 预览或修改 origin fetch/push URL；`apply=true` 触发确认 |

### 项目写操作

| 命令 | 用途 |
| --- | --- |
| `janus.proj.create` | 从模板仓复制工作树并 `git init` 为独立仓 |
| `janus.proj.rename` | 经确认同步改名已登记项目展示名与工作树目录；仓内主线仍为 `main` |
| `janus.proj.delete` | 删除项目目录 |
| `janus.proj.commit` | 提交指定项目 |
| `janus.proj.push` | 推送指定项目；仓库没有 origin 时先在 GitHub 上按项目编号建仓再推送（`visibility=public\|private`，默认 `public`） |
| `janus.proj.commitall` | 批量提交各项目仓 |
| `janus.proj.pushall` | 批量推送各项目仓；同样对缺 origin 的项目按编号建远端（`visibility` 同上） |
| `janus.proj.open` | 请求打开项目位置 |
| `janus.proj.repair` | 逐仓诊断独立仓与 status；不自动删盘 |
| `janus.proj.note` | 写入项目历史说明 |
| `janus.proj.metaopen` | 打开项目 Meta 目录 |
| `janus.history.rollback` | 回滚到指定历史节点 |
| `janus.history.reset` | 重置到指定历史节点 |
| `janus.history.forcepush` | 强制推送历史状态 |
| `janus.gitrule.excludes` | 改写全库共用的不纳入仓库清单（整体替换，需确认） |

写操作必须尊重宿主返回的确认要求，不能通过直接调用 Janus 内部服务绕过确认。MCP 只允许投影只读命令；Janus 不再公开 `debug` 类诊断命令。

## 数据与生命周期

- Janus 在宿主数据根下使用 `HistoryJanus` 子目录；消费者不得假设绝对 `%APPDATA%` 路径。
- 模块卸载时宿主撤销所有来源为 `module:HistoryJanus` 的命令并移除六个描述化页面。
- 热重载以完整模块快照替换旧注册；消费者不得长期缓存 Janus 服务实例或页面引用。
- Janus 不公开旧 `OneHistoryStudio.exe`、`--service-host`、独立 Web/MCP 地址或旧进程名合同。


## 场景页面注册（5.7.1）

REQ-SCENE-REG：页面显式声明 `scene=HistoryJanus`，注册初值只用于本模块场景。用户保存的各场景完整布局（包括分栏、位置、比例和隐藏状态）优先；模块刷新不得主动打开其他场景的页面。需要 Aurora 1.21.1 的场景注册隔离。
