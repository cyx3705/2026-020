# HistoryJanus

> 项目、Git 与 GitHub 治理：项目库、提交同步与分支图谱

![OneHistory Logo](./Logo.png)

## 定位

HistoryJanus 是运行在 HistoryVulcan 中的项目与 Git 治理模块：管理 HistoryClio 项目库的登记、提交、同步、归档，
Git 文件规则与 LFS，GitHub 账号与远端，以及分支图谱与提交历史。

- 宿主负责 Shell、命令总线、模块生命周期与 MCP；Janus 只注册业务命令和描述化页面（由 HistoryAurora 渲染）。
- 模块不自建窗口：提交预览、差异预览等一律走 `aurora.ui.dialog`。

## 概况

| 项 | 值 |
| --- | --- |
| 编号 | `2026-020` |
| 角色 | 宿主模块（`kind=module`） |
| 指令域 | `janus`（`janus.<类>.<方法>` 三段式） |
| 界面 | Aurora 描述化页面 |
| MCP 投影 | `readonly`；写命令不进 MCP |
| 版本与宿主下限 | [`JanusVersion.props`](./b-Code-Studio/JanusVersion.props) |

## 能力

| 类 | 指令 | 用途 |
| --- | --- | --- |
| `proj` | `list` / `scan` / `tree` / `diff` / `metas` … | 项目库只读查询 |
| `proj` | `create` / `commit` / `push` / `pull` / `sync` / `archive` … | 项目写操作（需确认，不进 MCP） |
| `history` | `list` / `show` / `diff` / `rollback` / `reset` … | 提交历史查看与回退 |
| `graph` | `summary` / `branches` / `commits` / `node` | 分支图谱 |
| `gitrule` | `list` / `lfs` / `excludes` | Git 文件规则与本仓实际 LFS 文件 |
| `github` | `accounts` / `status` / `test` / `remote` / `login` … | GitHub 账号与远端 |
| `ui` | `describe` / `actions` / `data` … | 页面协议（内部） |

完整命令目录（运行时共 51 条）、参数与返回见 [模块 API](./b-Office/package/模块API.md)。

## 入口

| 入口 | 用途 |
| --- | --- |
| [`AGENTS.md`](./AGENTS.md) | AI 工作合同：读取顺序、真值判定、边界 |
| [`project.manifest.json`](./project.manifest.json) | 项目身份、活动目录、文档与命令 |
| [文档中心](./b-Office/文档中心.md) | 文档索引与读取顺序 |
| [项目概览](./b-Office/current/项目概览.md) | 目标、范围与状态 |
| [技术合同](./b-Office/current/技术合同.md) | 现行需求与架构 |
| [有效决策](./b-Office/current/有效决策.md) | 仍然有效的关键决策 |
| [验证合同](./b-Office/current/验证合同.md) | 验证层级、命令与证据 |
| [模块 API](./b-Office/package/模块API.md) | 跨模块消费合同 |
| [指令优化规范](./b-Office/current/指令优化规范.md) | 命令命名规则与旧名映射 |

## 目录

| 路径 | 职责 |
| --- | --- |
| `b-Code-Studio/` | 业务源码、模块入口与 `eng/` 构建门禁脚本 |
| `b-Code-Verify/` | Contracts、功能 Smoke 与 ModuleSmoke |
| `b-Office/` | 项目文档：`current/` 现行合同、`package/` 消费合同、`history/` 只读归档 |
| `z-Publish/` | 正式快照与 `history/` 归档，由宿主管线写入 |

## 构建与验证

```powershell
dotnet restore .\HistoryJanus.sln --locked-mode -p:NuGetAudit=false
dotnet build .\HistoryJanus.sln -c Release --no-restore -p:NuGetAudit=false
dotnet test .\b-Code-Verify\Contracts\Contracts.csproj -c Release -p:NuGetAudit=false
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code-Studio\eng\Test-QualityGate.ps1
dotnet run --project .\b-Code-Verify\ModuleSmoke\ModuleSmoke.csproj -c Release -- .\z-Publish
```

`Test-QualityGate.ps1` 日常化七项漂移检查：抑制标记、千行文件、版本链一致性、Git/规则交互、模块 API 投影、
正式树边界与宿主合同。推送时 [`historyjanus-gate.yml`](./.github/workflows/historyjanus-gate.yml) 在 GitHub Actions 上复验。

## 开发与发布

改动只进 `vulcan.dev.start` 创建的工作区，经宿主 Console CLI 走
`vulcan.dev.start` → `vulcan.dev.submit`（候选构建并热装送审）→ `vulcan.dev.finish`（批准后并回并写入 `z-Publish`）。
本仓不自行发布；`Build-HistoryJanusPackage.ps1` 只用于本地候选构建。

## 要点

- 正式快照含 `HistoryJanus.dll`、XML、module manifest、checksum 与 `docs/`，不含 Janus EXE 或 HistoryVulcan 运行库。
- 跨项目读取已发布 API 走 `diana.docs.read domain=janus`；HistoryVulcan 合同只从平级 `2026-023-HistoryVulcan/z-Publish` 消费。
- 默认不读 `b-Office/history`，只有明确追溯版本时才读指定文件。

---

作者：Pinavia
