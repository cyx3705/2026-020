# HistoryJanus 源码

本目录拥有 Janus 业务源码和模块发布管道。当前正式交付工程是 `Module/HistoryJanus.Module.csproj`；旧 EXE、Service、Connection 和启动入口已删除，生产边界只保留模块源码。

## 权威入口

- `JanusVersion.props`：唯一 Janus 版本源。
- `Module/module.manifest.json`：模块装载 manifest，版本必须与版本源一致。
- `Module/HistoryJanusUiModule.cs`：宿主上下文、命令与两个页面的组合入口（分支历史内嵌于项目操作）。
- `StudioBusinessComposition.cs`：项目、Git 规则、历史和诊断命令组合。
- `eng/Build-HistoryJanusPackage.ps1`：本地写出 `z-Publish/HistoryJanus-vX.Y.Z/` 供门禁核对。正式写入 z、提交、装机只走宿主开发管线。

## 发布边界

`z-Publish/HistoryJanus-vX.Y.Z/` 是当前模块候选；`z-Publish/history` 只保存被替换的正式包。正式包不得包含 EXE、PDB、HistoryVulcan DLL、deps/runtimeconfig 或旧综合 Help。Diana 不发布。

```powershell
.\b-Code-Studio\eng\Build-HistoryJanusPackage.ps1
HistoryVulcan.Cli.exe --cli vulcan.dev.submit name=HistoryJanus msg=<说明> worktree=<工作区>
```

发布和部署不启动 HistoryVulcan，不修改或测试开机自启动。
