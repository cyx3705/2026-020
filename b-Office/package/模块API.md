# HistoryJanus 3.5 模块 API

本文件是其他模块和项目消费 HistoryJanus 的唯一人工合同。运行时命令目录是参数、确认策略和可用性的最终真值；历史文档和 Janus 内部类型不构成公开 API。

## 正式消费入口

- 正式快照：`z-HistoryJanus`。
- 模块名：`HistoryJanus`。
- 版本：`3.5.0`。
- 入口：`HistoryJanus.dll`。
- 宿主基线：HistoryVulcan `3.2.2` current-host 快照，从 `2026-023-HistoryVulcan/z-HistoryVulcan` 消费；该快照的 `sourceDirty` 仍由 HistoryVulcan manifest 如实标记。
- 主题：页面使用 HistoryVulcan `Shell.Brush.*` 动态资源，跟随宿主深色/浅色切换，不在模块内维护第二套主题。
- 命令来源：`module:HistoryJanus`。
- 命令命名：`janus.<类>.<方法>` 三段式全小写（详见 `b-Office/current/指令优化规范.md`）。
- UI：启用。
- MCP：只读投影。

其他项目只读取 z 级快照中的本文件、`module.manifest.json` 和 `SHA256SUMS`。不要从 `b-Publish`、Janus 的 `bin/obj`、HistoryVulcan 工作树或 Janus 历史文档建立依赖。

## 宿主接入

Janus 实现 `IUiModule`、`IShellUiAware` 和 `IModuleContextAware`。HistoryVulcan 注入 `IModuleContext` 后，Janus 使用其中的 `Bus`、`Settings`、`Log`、`DataDirectory` 与命令注册事务。消费者模块通过自己的宿主上下文取得同一个 `CommandBus`，按命令名调用 Janus；不得构造 Janus 服务、引用内部 DTO，或自行加载 Janus DLL。

```csharp
var result = await context.Bus.ExecuteAsync("janus.proj.list", "filter=2026");
if (!result.Success)
    throw new InvalidOperationException(result.Message);
```

结构化结果跨宿主 HTTP 边界时可能表现为 `JsonElement`。消费者应按运行时目录说明投影为自己的 DTO，不依赖 Janus 页面内部模型。

## UI 窗口

| ID | 标题 | 默认位置 | 用途 |
| --- | --- | --- | --- |
| `overview` | 项目总览 | 左侧 | 项目列表、z/Z 级元文件夹与共享项目选择 |
| `projops` | 项目操作 | 右侧 | 创建、提交、推送，Git 文件规则与内嵌分支历史同级切换 |
| `github` | github | 右侧 | 服务器 GitHub 凭据、SSH、提交身份、origin 与连接诊断 |

三个 ID 是布局兼容合同。其他模块不能重复注册这些 ID；需要联动项目选择时应通过 Janus 命令读取事实，不访问页面私有状态。3.2.0 起撤销 `tree`、`meta`；3.3.0 起撤销 `history`；3.4.0 起并入 `github` 窗口（旧 ID `github.account` 撤销）。GitHub 写操作（登录、注销、提交身份、origin 修改）维持仅限页面内经确认执行，不进入命令总线。

## 命令目录

3.5.0 为破坏性改名：旧名（`proj.*` / `git.rule.*` / `github.*` / `debug.*` / `HistoryJanus.Status`）一次作废，不留别名。完整映射见 `指令优化规范.md`。

### 模块与读取

| 命令 | 模式 | 用途 |
| --- | --- | --- |
| `janus.status` | 只读 | 返回模块身份和注册状态 |
| `janus.proj.list` | 只读 | 列出项目工作树 |
| `janus.proj.tree` | 只读 | 读取或刷新继承树 |
| `janus.proj.scan` | 只读 | 扫描项目大文件 |
| `janus.proj.config` | 只读 | 返回项目命令配置 |
| `janus.meta.list` | 只读 | 列出项目 z/Z 级元文件夹 |
| `janus.history.list` | 只读 | 列出分支自有提交 |
| `janus.history.show` | 只读 | 读取提交详情 |
| `janus.history.diff` | 只读 | 预览历史节点与 HEAD 的差异 |
| `janus.gitrule.list` | 只读 | 列出 Git 文件规则与索引状态 |
| `janus.gitrule.scan` | 只读 | 扫描格式台账和覆盖率 |
| `janus.gitrule.review` | 只读 | 查看未决格式与规则建议 |
| `janus.github.status` | 只读 | 服务器 Git、GCM、提交身份、origin 和 SSH 状态 |
| `janus.github.accounts` | 只读 | 列出 GCM 中已知的 GitHub HTTPS 凭据账号 |
| `janus.github.test` | 只读 | 检测 GitHub SSH/HTTPS 连接（`transport=auto\|ssh\|https`，`timeout=1..120`），不执行 push |

### 项目写操作

| 命令 | 用途 |
| --- | --- |
| `janus.proj.create` | 创建编号项目工作树 |
| `janus.proj.delete` | 删除项目工作树 |
| `janus.proj.commit` | 提交指定项目 |
| `janus.proj.push` | 推送指定项目 |
| `janus.proj.commitall` | 批量提交项目 |
| `janus.proj.pushall` | 批量推送项目 |
| `janus.proj.open` | 请求打开项目位置 |
| `janus.proj.repair` | 修复项目工作树 |
| `janus.proj.note` | 写入项目历史说明 |
| `janus.meta.open` | 打开项目 Meta 目录 |
| `janus.history.rollback` | 回滚到指定历史节点 |
| `janus.history.reset` | 重置到指定历史节点 |
| `janus.history.forcepush` | 强制推送历史状态 |
| `janus.gitrule.sync` | 同步模板规则基线 |
| `janus.gitrule.set` | 保存单条 Git 文件规则 |
| `janus.gitrule.batchset` | 原子保存多条 Git 文件规则 |
| `janus.gitrule.remove` | 删除 Git 文件规则 |

### 诊断

| 命令 | 用途 |
| --- | --- |
| `janus.debug.logflood` | 生成限量日志负载 |
| `janus.debug.sleep` | 生成可取消延时任务 |

写操作必须尊重宿主返回的确认要求，不能通过直接调用 Janus 内部服务绕过确认。MCP 只允许投影只读命令，诊断命令不作为跨模块稳定业务合同。

## 数据与生命周期

- Janus 在宿主数据根下使用 `HistoryJanus` 子目录；消费者不得假设绝对 `%APPDATA%` 路径。
- 模块卸载时宿主撤销所有来源为 `module:HistoryJanus` 的命令并移除三个窗口。
- 热重载以完整模块快照替换旧注册；消费者不得长期缓存 Janus 服务实例或页面引用。
- Janus 不公开旧 `OneHistoryStudio.exe`、`--service-host`、独立 Web/MCP 地址或旧进程名合同。

## 历史备注

- V3.1.0：模块由 `OneHistoryStudio` 改名为 `HistoryJanus`。
- V3.3.2：正式目录改名为 `z-HistoryJanus`，API 文档位于 `docs/`。
- V3.4.0：GitHubConnection 并入为 `github` 页面与 `github.*` 命令。
- V3.5.0：全部指令改为 `janus.<类>.<方法>`；github 页删除隐式 Button 样式并补齐 DataGrid Surface 刷子。
