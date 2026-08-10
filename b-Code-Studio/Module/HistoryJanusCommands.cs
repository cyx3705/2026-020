using System.Reflection;
using HistoryVulcan.Core.Modules;

namespace HistoryJanus.Module;

/// <summary>Reports the loaded Janus module identity and registered business surfaces.</summary>
public sealed class HistoryJanusCommands
{
    // 版本号的唯一权威源是 JanusVersion.props，经程序集 InformationalVersion 投影到此处；
    // 不得在本类硬编码版本常量（代码管道化:版本身份单源,下游只读投影）。
    [ModuleCommand(Readonly = true, CommandClass = "core")]
    public string status()
    {
        var version = typeof(HistoryJanusCommands).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(HistoryJanusCommands).Assembly.GetName().Version?.ToString(3)
            ?? "unknown";
        return $"HistoryJanus {version} 已由 HistoryVulcan 加载;已注册项目总览和项目操作两个页面;"
               + "Git 文件规则、分支历史与 GitHub 在项目操作页底部同一行分段切换;"
               + "Meta 文件夹已合并到项目总览;指令域 janus 含 proj/gitrule/history/github 四类,"
               + "写操作仅限页面或确认门;业务命令已接入宿主命令总线";
    }
}
