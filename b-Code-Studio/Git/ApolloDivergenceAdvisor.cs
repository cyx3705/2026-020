using System.Text;
using System.Text.Json;
using HistoryVulcan.Core.Commands;

namespace HistoryJanus.Git;

/// <summary>
/// 5.15.0：经命令总线请 HistoryApollo 判断一次分叉能不能自动合。
///
/// **软依赖**：不引用 Apollo 的程序集，只发一行 <c>apollo.chat.send json=true</c>。
/// 用 send 而不是 ask，是因为差异和提交列表带换行，消息数组走 JSON 后整条指令是一行。
/// 用安静执行（InvokeAsync）：控制台里提问与答复由 Apollo 自己逐轮报，这里不再回显上万字的指令文本。
/// Apollo 没装、没密钥、断网、答非 JSON，都折成 <see cref="DivergenceAdvice.Failure"/>，调用方一律不执行。
/// </summary>
public sealed class ApolloDivergenceAdvisor(ICommandBus bus) : IDivergenceAdvisor
{
    public const string CommandSource = "module:HistoryJanus";

    /// <summary>
    /// 文本冲突在问 AI 之前已经被 git 排除，AI 只管「git 看不出来的」：两边改了同一处语义、
    /// 改名或删除撞上另一边的修改、版本号/清单这类必须单一取值的字段被两边各改一次。
    /// 机器人生成物与手工内容各改各的目录，是最典型的可以直接合的情况。
    /// 提示里必须出现 json 字样——DeepSeek 的 JSON 输出模式要求如此。
    /// </summary>
    private const string SystemPrompt =
        "你是 Git 合并审查员。本地分支与远端分支已分叉，git 已在内存中试合确认没有文本冲突。"
        + "你要判断自动合并是否安全：只有两边改动互不相干、合并后不会产生语义矛盾时才算安全。"
        + "以下情况判为不安全：两边改了同一逻辑（同一函数、同一配置项、同一段文档的同一事实）；"
        + "一边删除或改名了另一边还在修改或引用的内容；版本号、清单、锁文件这类只能有一个取值的字段被两边各自改了。"
        + "远端是自动化机器人刷新生成物、本地是手工改动且目录不交叠，属于安全。拿不准时判为不安全。"
        + "只输出一个 json 对象：{\"safe\": true 或 false, \"reason\": \"一句中文理由\"}，不要输出其他文字。";

    public async Task<DivergenceAdvice> AdviseAsync(DivergenceFacts facts, CancellationToken cancellation)
    {
        var messages = JsonSerializer.Serialize(new[]
        {
            new { role = "system", content = SystemPrompt },
            new { role = "user", content = BuildPrompt(facts) },
        });
        var command = "apollo.chat.send json=true temperature=0 maxtokens=300 timeout=120"
                      + " messages=" + CommandParser.QuoteArg(messages);
        var result = await bus.InvokeAsync(command, CommandSource, cancellation).ConfigureAwait(false);
        return Read(result);
    }

    internal static string BuildPrompt(DivergenceFacts facts)
    {
        var text = new StringBuilder();
        text.Append("分支：").Append(facts.Branch).Append("；分叉点：").Append(facts.MergeBase)
            .Append("；拟用方式：").Append(facts.Strategy).Append('\n');
        Section(text, "本地独有提交", facts.LocalCommits);
        Section(text, "远端独有提交", facts.RemoteCommits);
        Section(text, "本地改动文件", facts.LocalFiles);
        Section(text, "远端改动文件", facts.RemoteFiles);
        Section(text, "两边都改过的文件", facts.OverlapFiles);
        if (facts.OverlapDiff.Length > 0)
            text.Append("交叠文件的差异：\n").Append(facts.OverlapDiff).Append('\n');
        text.Append("请判断能否自动合并，按 json 输出。");
        return text.ToString();
    }

    /// <summary>把 Apollo 的回执读成判定。json=true 时回执正文就是模型答复本身。</summary>
    internal static DivergenceAdvice Read(CommandResult result)
    {
        if (!result.Success)
            return new DivergenceAdvice(false, string.Empty, FirstLine(result.Message));
        try
        {
            using var document = JsonDocument.Parse(result.Message);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("safe", out var safe)
                || safe.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return new DivergenceAdvice(false, string.Empty, "模型答复缺少 safe 字段：" + FirstLine(result.Message));
            var reason = root.TryGetProperty("reason", out var why) && why.ValueKind == JsonValueKind.String
                ? why.GetString()!.Trim()
                : string.Empty;
            return new DivergenceAdvice(safe.GetBoolean(), reason.Length == 0 ? "（未给理由）" : reason);
        }
        catch (JsonException)
        {
            return new DivergenceAdvice(false, string.Empty, "模型答复不是 JSON：" + FirstLine(result.Message));
        }
    }

    private static void Section(StringBuilder text, string title, IReadOnlyList<string> items)
    {
        text.Append(title).Append('（').Append(items.Count).Append("）：");
        text.Append(items.Count == 0 ? "无" : "\n  " + string.Join("\n  ", items)).Append('\n');
    }

    private static string FirstLine(string? message)
    {
        var line = (message ?? string.Empty).Split('\n')[0].Trim();
        return line.Length <= 200 ? line : line[..200] + "…";
    }
}
