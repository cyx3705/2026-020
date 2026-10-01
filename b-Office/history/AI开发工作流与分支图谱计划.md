# HistoryJanus AI 开发工作流与分支图谱计划

更新日期：2026-08-16

## 计划定位

本计划把 HistoryJanus 从“项目与 Git 治理模块”扩展为 OneHistory 的 AI 开发工作台：
在 Janus 中统一查看项目集合的 Git 分支图、编号项目主线、AI 工作分支、提交、合并和验证状态，
并把宿主开发管线的状态接到同一条可审计工作流。

本计划只增加 Janus 的编排、图谱和界面能力；各模块仍拥有自己的源码、测试、合同和版本源。
发布与工作区由宿主 `vulcan.dev.*` 拥有；Diana 只提供文档通道，不发布。

## 目标与非目标

### 目标

1. 在项目集合中打开任一编号项目，显示从左到右展开的 Git 有向无环图（DAG）。
2. 把项目仓内的 `main` 作为主线，把本仓普通分支和 `ai/...` 工作分支显示为平行支线；项目继承关系保留在继承树，不混入提交泳道。
3. 把提交、分叉、合并、远端引用移动和 Diana 验证证据绑定到具体提交 SHA。
4. 以 Janus 作为可视入口查看 AI 工作区和验证状态；创建、提交、并回走宿主 `vulcan.dev.*`。
   工作区与发布状态的写入真值在宿主，不在 Diana，也不在 Janus。
5. 保持 Janus、Diana、Vulcan、Mercury、Minerva 的模块边界和独立发布节奏。

### 非目标

- 不在 Janus 内复制宿主开发管线的构建、测试、快照提升和回滚实现。
- 不新建第二套 GitRunner、命令总线、MCP 网关、服务宿主或跨模块 CLR API。
- 不把 `fetch`、`push` 等引用移动伪装成提交；只有真实 commit 才是提交节点。
- 不自动批准、强制合并、强制删除或绕过宿主确认。

## 现有能力复用原则

实现前必须先复用下列现有组件，并以测试证明不足后才允许增加薄封装：

| 现有组件 | 复用方式 |
| --- | --- |
| `ProjectService` | 复用库根、受管路径校验、独立仓列举和项目主工作树状态读取；不据此复制 Diana 的 AI 工作区写路径 |
| `ProjectCommands` | 复用项目列表、创建、删除、提交、推送、修复和统一确认策略 |
| `BranchTreeService` | 只复用项目继承关系；提交图谱由 `GraphService` 从当前项目仓的 refs 与父边构建 |
| `BranchHistoryService` / `HistoryRecorder` | 复用分支提交历史、操作留痕和说明覆盖，不另建审计格式 |
| 宿主 `CommandBus` | 所有页面动作和跨模块调用均通过稳定命令名执行 |
| HistoryVulcan 开发管线 | 宿主拥有 AI 工作区、候选、装机和正式并回；Janus 只展示结果 |
| HistoryVulcan MCP/确认层 | MCP 投影、危险操作确认、模块生命周期和 Web/HTTP 边界继续由宿主拥有 |

Janus 页面不得直接加载 Diana DLL、读取 Diana 私有类型或拼接任意 PowerShell；跨模块只调用公开命令。

## 工作树与分支命名

AI 工作树根目录及分支命名由 Diana 的正式开发合同提供，Janus 不维护第二个根目录设置或序号分配器。
界面只展示 Diana 返回的绝对路径、基线 SHA 和分支名。当前约定的展示格式为：
AI 分支格式为：

```text
ai/<编号项目>/<基线短SHA>-<序号>-<目标简称>
```

例如：`ai/2026-021-HistoryMercury/a1b2c3d-1-fix-dock-layout`。

- 完整基线 SHA 保存在 Diana 的工作区记录，短 SHA 只用于展示和分支名。
- `<目标简称>` 只允许 ASCII 小写字母、数字和连字符。
- 序号由 Diana 按其正式合同分配，Janus 和 AI 都不自行猜测。
- AI worktree 必须是**本项目仓**的额外 linked worktree，不能是普通复制目录，也不能再挂到共享裸仓上。

## AI 工作任务状态

Diana 的工作区和发布记录是任务状态真值。Janus 不再写一份 `ai-worktrees.json` 与 Diana 竞争；只按公开
命令读取任务 ID、项目名、基线 SHA、分支名、绝对 worktree 路径、当前 HEAD、状态、验证证据和时间戳，
页面缓存只用于展示，不能反向证明任务已验证、已批准或已清理。

状态只能按以下方向推进：

```text
active -> ready -> verified -> approved -> merged -> cleaned
   \-> failed / abandoned
```

`verified`、`approved` 和 `merged` 均绑定完整 HEAD SHA。分支产生新提交后，旧验证和审批自动失效；
清理失败必须保留现场并标记 `cleanupFailed`，不得假报已清空。

## 用户工作流

1. 在 Janus 项目总览选择编号项目和基线节点。
2. Janus 请求 Diana 创建 AI 分支及隔离工作树，并展示 Diana 返回的任务 ID、路径和分支名。
3. AI 在该工作树中开发并通过现有提交命令保存提交。
4. Janus 展示提交图、dirty 状态、差异摘要和基线关系；AI 点击“准备验证”锁定当前 HEAD。
5. Janus 请求 Diana 对该 HEAD 执行候选构建与验证；`releaseId` 和证据由 Diana 保存，Janus 只读取展示。
6. 人工查看差异和证据后批准**指定 HEAD**。
7. Janus 在合并前重新确认工作树干净、HEAD 未变化、目标分支未漂移且无冲突，再执行合并。
8. 合并后的主线必须重新调用 Diana 正式候选验证；只有主线 HEAD 对应证据通过后才允许正式发布。
9. 合并完成且用户确认后，Janus 删除 AI linked worktree 和 AI 分支；任何失败均保留现场。

## 分支图谱模型

Git 真值是 DAG，不是线性日志。界面可以按时间从左到右布局，但数据必须保留：

- commit SHA、父提交 SHA 列表、作者、时间、标题、变更文件摘要；
- 本地/远端 ref、`main` 主线、普通本仓分支和 AI 分支类型；项目继承关系不进入提交泳道；
- merge commit 与 merge parent；
- fetch/push/ref 移动事件（不创建伪提交节点）；
- Diana 验证、人工审批和正式发布附着的 commit SHA。

首版只加载当前项目和可视窗口附近的提交，按需展开父节点/子节点；必须使用虚拟化，不能一次渲染整个项目集合。

## MCP 与命令暴露面

### Janus 图谱读取（首版）

- `janus.graph.summary`：项目、分支、HEAD、节点数和 dirty 状态摘要。
- `janus.graph.branches`：本仓 `main`、普通分支、AI 分支及其分叉/合并关系；项目继承树另行读取。
- `janus.graph.commits`：按项目、分支、时间和游标分页读取提交节点。
- `janus.graph.node`：读取单个提交、父边、差异摘要和关联证据。

### Janus AI 工作树生命周期

- `janus.aiworktree.create`：请求 Diana 创建任务、分支和隔离工作树。
- `janus.aiworktree.list`：列出任务状态和路径。
- `janus.aiworktree.status`：读取 dirty、HEAD、差异和验证匹配情况。
- `janus.aiworktree.ready`：锁定待验证 HEAD。
- `janus.aiworktree.approve`：人工批准指定验证证据对应的 HEAD。
- `janus.aiworktree.merge`：经宿主确认合并到目标编号分支。
- `janus.aiworktree.cleanup`：仅在合并或明确放弃后请求 Diana 清理 worktree；分支保留/删除规则以 Diana 合同为准。

以上命令仍是后续 Janus 可视编排面的计划，不属于 4.0.0 运行时命令。读取命令优先开放为只读 MCP；
创建、ready、approve、merge、cleanup 必须保留宿主确认，并且不得绕开宿主开发管线直接执行
工作区或发布写操作。

### 宿主协作面

工作区、候选构建、测试、装机和正式并回由宿主 `vulcan.dev.start` / `submit` / `finish` 拥有。
Janus 只展示结果，不复制管线，不调用已删除的 Diana 发布器。跨项目说明书仍经
`diana.docs.catalog` / `diana.docs.read` 读取。

## 分阶段实施

### Phase 0：接口与数据合同

- 冻结 AI worktree 注册记录、状态机、分支命名、releaseId 和证据绑定规则。
- 为图谱节点、边、ref、任务和验证证据建立内部模型；不暴露 Janus CLR 类型。
- 验收：Architecture/Contract Smoke 能验证状态转移和 HEAD 失效规则。

### Phase 1：只读分支图谱

- 状态：已完成。图谱使用独立 `graph` 窗口并入宿主控制台标签组；`overview` 保持项目列表，二者共享当前项目选择。
- 复用 `ProjectService`、`BranchTreeService`、`janus.history.*` 数据；先完成分页、虚拟化、节点详情和过滤。
- 验收：真实仓库与临时多分支仓库的父边、merge parent、主线/AI 分支分类一致；开放分支贴近分叉点，
  已合并历史分支贴近 merge；无写操作。

### Phase 2：AI worktree 生命周期

- 通过 Diana 正式命令读取和驱动工作区生命周期，不在 Janus 新增第二份注册表、路径根或 Git 写实现。
- 新增 `create/list/status/ready` 可视编排面时，只保存页面态和 Diana 任务 ID，不复制 GitRunner。
- 验收：创建、脏状态、提交、ready、取消和清理失败均能从 Diana 记录复验；Janus 缓存丢失不影响真值，
  越界路径和受保护分支由 Diana 拒绝并原样展示。

### Phase 3：Diana 候选验证协作

- Janus 发起候选任务并显示实时状态；Diana 继续拥有构建、测试、质量门禁、快照和镜像。
- 验收：验证证据绑定源 HEAD；HEAD 变化后 releaseId 自动失效；不允许拿候选证据直接冒充主线正式证据。

### Phase 4：审批、合并与清理

- 实现人工审批、合并前再检查、合并后重新验证和 worktree 清理。
- 合并冲突、审批过期、验证失败、删除失败都进入可恢复状态，不自动强推或强删。
- 验收：成功路径与每个失败路径均保留审计记录，合并后主线和宿主正式候选 SHA 一致。

## 独立发布与兼容策略

- Janus 先以独立版本交付图谱/工作树展示；工作区与发布由宿主 `vulcan.dev.*` 拥有。
- 未接入宿主管线时，Janus 保留只读图谱，不假装自己能发布。
- Diana 不发布；不得再调用已删除的集中发布脚本。
- Vulcan 只需提供稳定 CommandBus、确认、CLI 开发管线和模块生命周期；不为此功能增加项目专用宿主分支。
- Mercury、Minerva 不增加对 Janus 内部程序集的引用；需要联动时只消费公开命令或文档中心。

## 风险与门禁

- 工作树路径必须是本项目仓的额外 worktree 或登记的 AI 根下路径，拒绝重解析点和路径穿越。
- 审批永远绑定完整 SHA；任何新提交、rebase、reset 或目标分支漂移都使旧审批失效。
- 图谱读取必须分页并限制节点数，防止大型仓库拖垮 UI/MCP。
- 所有写命令经宿主确认；MCP 默认只读，危险动作不通过隐藏参数绕过确认。
- 发布管线失败时由 Diana 回滚候选/正式快照，Janus 只展示结果，不自行修复发布目录。

## 交付顺序

先完成 Phase 0 和 Phase 1，再进入 Phase 2；Phase 3、Phase 4 必须在 Diana release 接口和证据合同冻结后实施。
每一阶段单独构建、测试、发布和升级 Janus，禁止把四个模块绑定成一个同步版本。
