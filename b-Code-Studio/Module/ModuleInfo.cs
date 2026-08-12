using BaseVariable;

namespace HistoryJanus.Module;

public sealed class ModuleInfo : ModuleInfoBase
{
    public override string ModuleName => "HistoryJanus";
    // 指令域短拼：宿主按 CommandPrefix 投影 [ModuleCommand]，产出 janus.status
    public string CommandPrefix => "janus";
    public override string Description => "项目、Git 与 GitHub 治理";
    public override string Author => "OneHistory";
    public override string Version =>
        typeof(ModuleInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public override Type? MainClassType => typeof(HistoryJanusCommands);
}
