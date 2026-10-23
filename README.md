# HistoryJanus

> 项目、Git 与 GitHub 治理：项目库、提交同步与分支图谱

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

完整命令目录与参数是注册时的自描述：`diana.docs.read domain=janus`（宿主 6.1.0 起没有消费文档）。

## 入口

| 入口 | 用途 |
| --- | --- |
| [`AGENTS.md`](./AGENTS.md) | AI 工作合同：读取顺序、真值判定、边界 |
| [`project.manifest.json`](./project.manifest.json) | 项目身份、活动目录、文档与命令 |
| [现行约定](./b-Office/现行约定.md) | 唯一的长期文档：真相在哪、取舍、真机才知道的、工程约定 |

## 目录

| 路径 | 职责 |
| --- | --- |
| `b-Code-Studio/` | 业务源码、模块入口与 `eng/` 构建门禁脚本 |
| `b-Code-Verify/` | Contracts、功能 Smoke 与 ModuleSmoke |
| `b-Office/` | `current/现行约定.md`，仅此一份 |
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

- 正式快照含 `HistoryJanus.dll`、XML、module manifest 与 checksum，不含 docs、Janus EXE 或 HistoryVulcan 运行库。
- 跨项目查 Janus 指令走 `diana.docs.read domain=janus`；命名合同由 `CommandCatalogContractTests` 守住。HistoryVulcan 合同只从平级 `2026-023-HistoryVulcan/z-Publish` 消费。
- 不留 `history/`、方案、需求书。仍有效的结论写进现行约定、调用处注释或测试。

## 保留内容

### logo与作者

- 作者：pinavia
- ![logo](./Logo.png)

### 学习许可与权利保留

- 本项目允许个人、学校和研究者进行非商业性的学习、研究和引用。
- 未经著作权人书面许可，不得将项目内容用于商业开发、产品制造、商业服务、商业宣传、投标交付、毕业设计成品交付或再授权。
- 不得删除作者、来源、版本历史和第三方许可证声明，不得将本项目或其衍生成果宣称为与作者无关的原创项目。
- 软件源码适用 [PolyForm Noncommercial 1.0.0](https://polyformproject.org/licenses/noncommercial/1.0.0/)；机械模型、PCB、原理图、图片、文档、Unity 工程和其他非软件资料适用 [CC BY-NC-ND 4.0](https://creativecommons.org/licenses/by-nc-nd/4.0/)。
- SE、SW、STEP/STP 及其他三维模型仅允许个人或学校进行非商业学习、评审和研究；未经著作权人书面许可，不得用于加工、打样、制造、销售、量产、投标、产品交付、商业宣传、改作或再授权。
- 第三方组件、模板、元件库、外购件模型和工具仍受其原始许可证约束；本声明不扩大作者对第三方内容的权利。

商业使用、产品化、公司内部长期使用、转让或排他性授权需另行取得著作权人的书面许可。
