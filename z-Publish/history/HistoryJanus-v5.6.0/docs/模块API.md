# HistoryJanus 5.6.0 模块 API

本文件是其他模块和项目消费 HistoryJanus 的唯一人工合同。运行时命令目录是参数、确认策略和可用性的最终真值；历史文档和 Janus 内部类型不构成公开 API。

## 正式消费入口

- 正式快照：`z-Publish/HistoryJanus-v5.6.0/`。
- 模块名：`HistoryJanus`。
- 版本：`5.6.0`。
- 入口：`HistoryJanus.dll`。
- 宿主基线：HistoryVulcan `5.1.2` current-host 快照，从 `2026-023-HistoryVulcan/z-Publish` 消费；该快照的 `sourceDirty` 仍由 HistoryVulcan manifest 如实标记。
- 主题：页面使用 `Aurora.Brush.*` 动态资源，跟随宿主深色/浅色切换，不在模块内维护第二套主题。
- 弹窗：模块不得 `new Window`。提交预览、差异预览、恢复提交说明、GitHub 通知与诊断走 `aurora.ui.dialog`（`message` / `prompt` / `content`）。写操作警告仍走命令 `ConfirmPrompt`，由宿主确认策略弹出，页面不得再叠一层。
- 命令来源：`module:HistoryJanus`。
- 命令命名：`janus.<类>.<方法>` 三段式全小写（详见 `b-Office/current/指令优化规范.md`）。
- UI：启用。
- MCP：只读投影。

本文件描述活动源的 `5.6.0` 候选合同；只有用户另行授权经宿主 `vulcan.dev.submit` / `finish` 正式发布后，同版本 manifest 和二进制才会提升到
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

## 描述化页面（Aurora V1）

| ID | 标题 | 默认位置 | 用途 |
| --- | --- | --- | --- |
| `overview` | 项目总览 | 中央工作区 | “刷新”控制面板及“项目 / z 级文件夹 / 状态 / 动作 / 最近提交”五列表格；状态为普通文本，动作列为黄色可点击符号 |
| `graph` | 分支图谱 | 并入控制台标签组 | 页面描述中的 `swimlane` 提交 DAG；以 `tabTarget=console` 与宿主控制台同组 |
| `projops` | 项目操作 | 左侧（宽度占比 `0.38`） | 一块四行控制面板（项目名/改名、新项目名/新建、提交描述/提交/推送、子页面切换），下接一个 `switch` 容器 |

`projops` 里的 `switch` 容器 `janus-sections` 收下三块子内容，由面板第四行的轮换选项框
经 `janus.section` 通道切换（Aurora REQ-UI-045/046）。**它们不是页面，也没有页面 ID**：

| `case` | 内容 |
| --- | --- |
| `Git 文件规则` | 「刷新规则」按钮，加全库共用的排除清单与**当前选中项目**的托管块落地状态两张表 |
| `分支历史` | **当前选中项目**从分叉点到 HEAD 的自有提交（最多 200 条） |
| `GitHub` | 「刷新 GitHub」按钮，加 Git、GCM、提交身份、origin 与 SSH 的当前状态（项/值两列） |

三个 ID 是布局兼容合同。**默认落位**：`overview` 占中央工作区且不声明 `DefaultTabTarget`；`graph` 以 `Tab` 并入宿主 `console`（控制台由 HistoryVulcan 注册，不依赖 Mercury）；`projops` 停靠左侧、默认宽度占比 0.38。已保存的用户布局优先于默认落位。其他模块不能重复注册这些 ID；需要联动项目选择时应通过 Janus 命令读取事实，不访问页面私有状态。3.2.0 起撤销 `tree`、`meta`；3.3.0 起撤销 `history`；3.4.0 引入的 `github` 窗口在 3.7.0 退役，其内容并入 `projops` 底部分段；
5.4.5 曾把 `rules` / `history` / `github` 重新注册为三个 `side=bottom` 页面，由停靠层并成一个底部标签组；
**5.4.6 又把它们撤掉**，收进 `projops` 的 `switch` 容器（REQ-015，需 Aurora ≥ 1.8.17）。
标签组是三页、各占一条底边；收进一页之后版面只剩一条选项框，三块内容拿到同一块完整高度。
消费方若还按 `rules` / `history` / `github` 三个页面 ID 建立依赖，5.4.6 起它们**不再存在**。
3.9.1 新增 `graph`，3.9.2 改为并入控制台（DEC-013）。GitHub 写操作（登录、注销、提交身份、origin 修改）由页面通过 `janus.github.*` 命令总线执行并服从宿主确认策略；只读刷新和连接诊断可使用模块内连接服务。

## 命令目录

3.5.0 为破坏性改名：旧名（`proj.*` / `git.rule.*` / `github.*` / `debug.*` / `HistoryJanus.Status`）一次作废，不留别名。完整映射见 `指令优化规范.md`。

3.7.0 再次收敛指令类：`debug` 类整体退役（`janus.debug.logflood` 与宿主 `vulcan.log.flood` 重复，`janus.debug.sleep` 无调用点），`meta` 类并入 `proj`（`janus.meta.list` → `janus.proj.metas`，`janus.meta.open` → `janus.proj.metaopen`）。3.8.0 业务命令为 35 条、类为 `proj` / `gitrule` / `history` / `github` 四类；加上模块宿主投影的 `janus.status`，运行时命令总数为 36 条。

3.9.0（DEC-012）新增 `graph` 类四条只读 DAG 命令；3.9.1（DEC-013）改为独立图谱窗口与编号主线/平行泳道。4.0.0（DEC-015）存储引擎改为独立仓：`name=` 是已登记项目目录名，仓内主线为 `main`。4.3.0（DEC-022）增加 `janus.proj.rename`。5.0.0 规则面收敛后业务命令为 35 条。5.4.6 运行时总数为 41 条。5.5.0 增加 4 条项目生命周期命令与 3 条本机 UI 命令，运行时总数为 **48** 条；新增写入命令不进入 MCP 只读投影。

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
| `janus.ui.data` | 只读 | 按 `view` 返回页面数据；`projects` 保留 `name`、`isClean`、`subject` 并增加 `zFolders`、`status`、`statusSymbol`、`lifecycleAction`、`archived`、`lifecycleState`。单个 z/Z 文件夹的 `zFolders` 为真实名称；状态符号固定映射提交 `●`、推送 `↑`、同步 `↔`、归档 `□`、拉取 `↓`。`graph` / `history` / `rulestate` 缺 `name` 时不取数 |
| `janus.ui.graphnode` | 只读 | 将描述化图节点 ID 投影为提交详情 |
| `janus.ui.refreshrules` | 只读 | 「刷新规则」的落点：按节点重取 `rule-list` 与 `rule-state` 两张表。5.4.6 起两张表同处 `projops` 一页，按页刷会把分支历史与 GitHub 一起带上，而 `aurora.ui.refreshdata` 一次只收一个节点 |
| `janus.ui.refreshprojects` | 本机 UI | 重新查询远端并刷新项目表 |
| `janus.ui.projectaction` | 本机 UI | 重新校验行状态后执行提交、推送、同步、归档或拉取 |
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

## 历史备注

- V5.6.0：`overview` 页标题改为 `Janus`；`janus.ui.data` 增加 `query` / `year` 两个参数与
  `view=years` 候选视图（返回只有 `value` 一列的行集，首项 `全部`）。搜索匹配项目名或任一
  z 级文件夹名，年份匹配项目名开头四位；总览与分支历史一律最新优先；分支历史列去掉
  `sha` 与 `author`（**字段仍在行里**，只是不再有列）。`view=projects` 的取数不再接受
  页面侧的 `refresh`——查询远端由 `janus.projects.refresh` 动作发起。命令名、确认策略与
  页面 ID 不变。中央区落位语义由 Aurora 1.18.0 提供（工具页组），Janus 声明不变。
- V5.5.1：`projects` 数据行新增内部字段 `statusSymbol`；单个 z/Z 文件夹的 `zFolders` 从 `"1 个"` 改为真实目录名。状态文字改为普通文本，相邻动作列以固定黄色符号触发原 `janus.project.action`；命令、确认策略和页面 ID 不变。
- V3.1.0：模块由 `OneHistoryStudio` 改名为 `HistoryJanus`。
- V3.3.2：正式目录改名为 `z-Publish`，API 文档位于 `docs/`。
- V3.4.0：GitHubConnection 并入为 `github` 页面与 `github.*` 命令。
- V3.5.0：全部指令改为 `janus.<类>.<方法>`；github 页删除隐式 Button 样式并补齐 DataGrid Surface 刷子。
- V3.5.1：`CommandClass` 与命令名中间段对齐；清除宿主残留 `GitHubConnection` 独立域槽。
- V3.6.0：总览以工具页挂入中央标签组；分支名唯一权威与路径解析；github 单页化；总览增加最近提交列。
- V3.7.0：独立 github 窗口并入项目操作页；业务命令收敛为 31 条、运行时总计 32 条；Smoke 套件并行并具名超时。
- V3.9.0：新增 `graph` 类四条只读 DAG 命令；总览右侧内嵌提交图谱（`GraphView`）；业务命令 39 条、运行时总计 40 条。
- V3.9.1：图谱改为独立 `graph` 窗口；主线为当前编号分支；平行泳道保留已合并历史行，右侧不再画端点。
- V3.9.2：图谱并入控制台标签组；从合并第二父还原已删除平行分支的历史行；去掉左侧泳道图例。
- V3.9.3：新建/修复工作树时补齐裸标记覆盖（DEC-014）；图谱改为拖动背景平移，不再用滚轮。
- V3.9.4：总览切换项目时图谱与分支历史消抖加载，不再取消进行中的宿主请求，避免控制台刷「请求处理失败」。
- V4.0.0：存储引擎改为独立仓（DEC-015）；`name=` 为已登记项目目录名；仓内默认分支 `main`；DEC-014 作废；图谱 X 按 Git 父边逐点赋值（DEC-016）；平行泳道优先贴主线并复用空行（DEC-018）。
- V4.1.0：`push` / `pushall` 在仓库缺 `origin` 时按需创建 GitHub 同名仓库后再推送（DEC-019）。新增可选参数 `visibility`（`public` / `private`，默认 `public`）；命令数与窗口不变。令牌取自 GCM 已存的 HTTPS 凭据，模块不新增任何凭据配置项。`PushReport` 增加 `RemoteCreated` / `RemoteUrl` / `RemoteVisibility`，`BatchPushReport` 增加 `CreatedRemotes`；消费者按需投影，旧字段语义不变。
- V4.1.1：自动建仓写入 `origin` 的 URL 改为优先 `ssh_url`（DEC-020）——HTTPS 在大包（实测约 17MB 起）上会被连接重置。推送认证因此依赖 `~/.ssh` 密钥，而建仓仍走 HTTPS token，两条凭据链路不同。建仓成功但推送失败时，结果消息也会点名新建的仓库。`PushReport.RemoteUrl` 回显的是实际写入 origin 的 URL。
- V4.2.0：自动建仓的远端仓库名改为只取项目编号 `YYYY-NNN`（DEC-021）——GitHub 会吃掉仓库名里的非 ASCII 字符（请求 `2025-001-AGV洗轮机` 建出 `2025-001-AGV-`），而库内多数项目是中文名。本地项目目录名不变，远端与本地刻意不一致；需要对外展示的仓库由用户自行在 GitHub 改名。已按旧规则建出的仓库保持原名。
- V4.3.0：项目操作页第二行支持编辑当前项目名并确认改名；`janus.proj.rename` 同步更新项目展示名与工作树目录，保护仓内主线 `main` 不被误改。
- V5.3.2：按名字解析单个项目仓只检查库根下该直接子目录，不再为图谱/历史/提交列举全库并对其它仓发 git；分支历史用 `git log --skip/--max-count` 分页，远端标记用当前页 `--no-walk`，不再 `rev-list` 整段 origin。
- V5.4.8：同版本重建会被宿主不可变快照拒绝；命令面与页面 ID 相对 5.4.7 不变。
- V5.4.7：把已在源码里的 Aurora 面板协议 V3（`rows`）连同 z 目录形状
  `HistoryJanus-vX.Y.Z/`、宿主 `vulcan.dev.start/submit/finish` 专属发布，写成当前候选并升版本。
  命令面与页面 ID 相对 5.4.6 不变；活宿主装到 5.4.7 后，旧 5.4.6 槽不再是运行真值。
- V5.4.6（对布局消费方是破坏性的）：`rules` / `history` / `github` **三个页面 ID 撤销**，
  三块内容收进 `projops` 的 `switch` 容器 `janus-sections`，由控制面板第四行的轮换选项框
  经 `janus.section` 通道切换（REQ-015）。新增只读命令 `janus.ui.refreshrules`，
  运行时命令 40 → 41 条；「刷新 GitHub」改绑 `node=github-rows`。
  四页顶部的说明段落与面板里的说明控件一并删除。需 Aurora ≥ 1.8.17。
- V5.4.5：`projops` 重注册为三行控制面板；`rules` / `history` / `github` 三页回归为底部标签组；
  项目表经**选择通道** `janus.project` 驱动它们，图谱与历史因此跟着选中走
  （此前图谱固定画项目清单第一条，不报错，只是一直不对）。需 Aurora ≥ 1.8.16。
- V5.2.0：页面弹窗改走 `aurora.ui.dialog`（DEC-025）。删除 `HistoryPreviewDialog` / `RollbackMessageDialog` 与 GitHub `MessageBox`；写操作确认仍经 `ConfirmPrompt`，不在页面重复弹出。
- V5.1.0：`.gitignore` 托管块尾部固定豁免 `z-*` 正式消费快照（DEC-024），排除清单再激进也不会把跨项目消费的快照删出索引；默认清单扩充为覆盖构建产物（`packages/`、`*.dll`、`*.pdb`、`*.obj`、`*.cache` 等）。
- V5.0.0（破坏性）：Git 文件规则收敛为**一份全库共用的「不纳入仓库」清单**（设置 `proj.excludesuffixes`），只有"是否纳入仓库"一个维度（DEC-023）。`janus.gitrule.set` / `batchset` / `remove` / `sync` / `scan` / `review` 六条命令一次性退役、不留别名，新增 `janus.gitrule.excludes`；业务命令 40 → 35 条，运行时 41 → 36 条。LFS 完全退出规则面：Janus 不再按扩展名生成任何 `.gitattributes` LFS 属性，只在提交链路对超过 GitHub 100MB 硬限的**具体文件**弹确认后按精确路径 `git lfs track`。清单由提交链路自动刷进各仓 `.gitignore` 托管块，消费方不需要（也无法）手动下发。推送统一走一个出口：推显式分支名、`lfs.locksverify=false`、待推超 300MB 时按提交分批推。
