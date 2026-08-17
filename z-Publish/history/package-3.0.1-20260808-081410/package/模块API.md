# OneHistoryStudio 3.0 模块 API

本文件是其他模块和项目消费 OneHistoryStudio 的唯一人工合同。运行时命令目录是参数、确认策略和可用性的最终真值；历史文档和 OHS 内部类型不构成公开 API。

## 正式消费入口

- 正式快照：`z-Package-OneHistoryStudio`。
- 模块名：`OneHistoryStudio`。
- 版本：`3.0.1`。
- 入口：`OneHistoryStudio.dll`。
- 宿主基线：AppShell `3.1.9` current-host 快照，从 `2026-023-AppShell/z-Package-AppShell` 消费；该快照的 `sourceDirty` 仍由 AppShell manifest 如实标记。
- 主题：页面使用 AppShell `Shell.Brush.*` 动态资源，跟随宿主深色/浅色切换，不在模块内维护第二套主题。
- 命令来源：`module:OneHistoryStudio`。
- UI：启用。
- MCP：只读投影。

其他项目只读取 z 级快照中的本文件、`module.manifest.json` 和 `SHA256SUMS`。不要从 `b-Publish`、OHS 的 `bin/obj`、AppShell 工作树或 OHS 历史文档建立依赖。

## 宿主接入

OHS 实现 `IUiModule`、`IShellUiAware` 和 `IModuleContextAware`。AppShell 注入 `IModuleContext` 后，OHS 使用其中的 `Bus`、`Settings`、`Log`、`DataDirectory` 与命令注册事务。消费者模块通过自己的宿主上下文取得同一个 `CommandBus`，按命令名调用 OHS；不得构造 OHS 服务、引用内部 DTO，或自行加载 OHS DLL。

```csharp
var result = await context.Bus.ExecuteAsync("proj.list", "filter=2026");
if (!result.Success)
    throw new InvalidOperationException(result.Message);
```

结构化结果跨宿主 HTTP 边界时可能表现为 `JsonElement`。消费者应按运行时目录说明投影为自己的 DTO，不依赖 OHS 页面内部模型。

## UI 窗口

| ID | 标题 | 默认位置 | 用途 |
| --- | --- | --- | --- |
| `overview` | 项目总览 | 中央 | 项目列表与选择 |
| `tree` | 继承树 | `overview` 标签组 | 分支继承关系 |
| `meta` | Meta文件 | `overview` 标签组 | z/Z 级项目元文件夹 |
| `projops` | 项目操作 | 右侧 | 创建、提交、推送和 Git 文件规则 |
| `history` | 分支历史 | 左侧 | 提交历史、差异与回滚 |

五个 ID 是布局兼容合同。其他模块不能重复注册这些 ID；需要联动项目选择时应通过 OHS 命令读取事实，不访问页面私有状态。

## 命令目录

### 模块与读取

| 命令 | 模式 | 用途 |
| --- | --- | --- |
| `OneHistoryStudio.Status` | 只读 | 返回模块身份和注册状态 |
| `proj.list` | 只读 | 列出项目工作树 |
| `proj.tree` | 只读 | 读取或刷新继承树 |
| `proj.scan` | 只读 | 扫描项目大文件 |
| `proj.config` | 只读 | 返回项目命令配置 |
| `proj.metalist` | 只读 | 列出项目 z/Z 级元文件夹 |
| `proj.history` | 只读 | 列出分支自有提交 |
| `proj.history.show` | 只读 | 读取提交详情 |
| `proj.history.diff` | 只读 | 预览历史节点与 HEAD 的差异 |
| `git.rule.list` | 只读 | 列出 Git 文件规则与索引状态 |
| `git.rule.scan` | 只读 | 扫描格式台账和覆盖率 |
| `git.rule.review` | 只读 | 查看未决格式与规则建议 |

### 项目写操作

| 命令 | 用途 |
| --- | --- |
| `proj.create` | 创建编号项目工作树 |
| `proj.delete` | 删除项目工作树 |
| `proj.commit` | 提交指定项目 |
| `proj.push` | 推送指定项目 |
| `proj.commitall` | 批量提交项目 |
| `proj.pushall` | 批量推送项目 |
| `proj.open` | 请求打开项目位置 |
| `proj.repair` | 修复项目工作树 |
| `proj.note` | 写入项目历史说明 |
| `proj.metaopen` | 打开项目 Meta 目录 |
| `proj.rollback` | 回滚到指定历史节点 |
| `proj.reset` | 重置到指定历史节点 |
| `proj.forcepush` | 强制推送历史状态 |
| `git.rule.sync` | 同步模板规则基线 |
| `git.rule.set` | 保存单条 Git 文件规则 |
| `git.rule.batch-set` | 原子保存多条 Git 文件规则 |
| `git.rule.remove` | 删除 Git 文件规则 |

### 诊断

| 命令 | 用途 |
| --- | --- |
| `debug.logflood` | 生成限量日志负载 |
| `debug.sleep` | 生成可取消延时任务 |

写操作必须尊重宿主返回的确认要求，不能通过直接调用 OHS 内部服务绕过确认。MCP 只允许投影只读命令，诊断命令不作为跨模块稳定业务合同。

## 数据与生命周期

- OHS 在宿主数据根下使用 `OneHistoryStudio` 子目录；消费者不得假设绝对 `%APPDATA%` 路径。
- 模块卸载时宿主撤销所有来源为 `module:OneHistoryStudio` 的命令并移除五个窗口。
- 热重载以完整模块快照替换旧注册；消费者不得长期缓存 OHS 服务实例或页面引用。
- OHS 不公开旧 `OneHistoryStudio.exe`、`--service-host`、独立 Web/MCP 地址或旧进程名合同。

## 兼容规则

- `3.x` 内保持模块名、窗口 ID 和既有命令名；新增可选命令或参数属于兼容扩展。
- 删除或改变命令语义、窗口 ID、结果字段或确认策略需要提升主版本并更新本文件。
- 正式消费前必须验证 `SHA256SUMS`；API 文档只说明合同，不能替代模块 manifest 与文件哈希校验。
