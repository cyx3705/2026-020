# Janus Verification

本目录是 HistoryJanus 的独立验证组件，产品源码位于平级 `b-Code-Studio`。验证工程可以引用产品工程，
产品工程不得反向引用验证组件。

## 结构

- `Contracts/`：由 `dotnet test` 发现的编译期 API 与合同测试。
- `Smoke/`：单一宿主承载的功能集成冒烟测试。
- `Smoke/Suites/`：按用户可观察功能分组的 7 个 Suite。
- `ModuleSmoke/`：HistoryVulcan UI/Service 双模式装载、命令、三个页面（图谱独立窗口、分支历史内嵌）、卸载与热重载测试。

## 执行

```powershell
dotnet test .\b-Code-Verify\Contracts\Contracts.csproj -c Debug
dotnet test .\b-Code-Verify\Contracts\Contracts.csproj -c Release
dotnet run --project .\b-Code-Verify\Smoke\Smoke.csproj -c Debug
dotnet run --project .\b-Code-Verify\Smoke\Smoke.csproj -c Release
dotnet run --project .\b-Code-Verify\ModuleSmoke\ModuleSmoke.csproj -c Release -- .\b-Code-Studio\Module\bin\Release\net8.0-windows
```

可通过 `--suite <功能名>` 定向执行，例如：

```powershell
.\b-Code-Verify\Smoke\bin\Debug\net8.0-windows\Smoke.exe --suite GitRules
.\b-Code-Verify\Smoke\bin\Debug\net8.0-windows\Smoke.exe --suite RepositoryTargets
```

日常功能完成由产品脚本组合 Contracts、Debug 构建、定向 Smoke 和可运行测试部署：

```powershell
```

产物只覆盖 `b-Publish/current/HistoryJanus`，不保留候选历史。完整 Debug/Release Smoke、ModuleSmoke
及正式发布校验由 Diana 集中发布脚本执行；本项目只保留候选构建入口。

默认 Smoke 禁止真实鼠标、开机自启动、UAC、正式项目写入和第二台 LAN 设备。项目操作页行为使用隔离数据和 STA 测试线程执行：

```powershell
.\b-Code-Verify\Smoke\bin\Debug\net8.0-windows\Smoke.exe --suite ProjectOperations
```

Suite 使用功能名称，`RunAsync` 只编排同功能场景；临时数据统一进入系统临时目录，PASS/FAIL 由 runner
输出。单个 Suite 源文件上限为 550 行，超出后使用同功能 `partial` 文件拆分。
