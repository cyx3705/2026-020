using System.Text;
using HistoryVulcan.Core.Commands;

namespace HistoryJanus.Views;

/// <summary>
/// 把页面弹窗编成 <c>aurora.ui.dialog</c>。模块不自己 <c>new Window</c>；
/// 写操作确认仍走命令 <c>ConfirmPrompt</c>，页面不得再叠一层。
/// </summary>
public static class AuroraDialog
{
    public const string CommandName = "aurora.ui.dialog";

    public static string Message(string title, string body)
        => Build("message", title, body);

    public static string Content(string title, string body, string content)
        => Build("content", title, body, content: content);

    public static string Prompt(string title, string body, string value, string? primary = null)
        => Build("prompt", title, body, value: value, primary: primary);

    public static Task ShowMessageAsync(CommandBus bus, string title, string body)
        => bus.ExecuteAsync(Message(title, body), "UI");

    public static Task ShowContentAsync(CommandBus bus, string title, string body, string content)
        => bus.ExecuteAsync(
            Content(title, body, string.IsNullOrWhiteSpace(content) ? "(无差异内容)" : content),
            "UI");

    public static async Task<string?> PromptAsync(
        CommandBus bus, string title, string body, string value, string? primary = null)
    {
        var result = await bus.ExecuteAsync(Prompt(title, body, value, primary), "UI");
        if (!result.Success)
            return null;
        var text = result.Message.Trim();
        return text.Length == 0 ? null : text;
    }

    public static string Build(
        string kind,
        string title,
        string body,
        string? content = null,
        string? value = null,
        string? primary = null,
        string? cancel = null,
        bool danger = false,
        bool defaultCancel = false,
        int? timeout = null)
    {
        var command = new StringBuilder(CommandName)
            .Append(" kind=").Append(kind);
        Append(command, "title", title);
        Append(command, "body", body);
        Append(command, "content", content);
        Append(command, "value", value);
        Append(command, "primary", primary);
        Append(command, "cancel", cancel);
        if (danger)
            command.Append(" danger=true");
        if (defaultCancel)
            command.Append(" defaultcancel=true");
        if (timeout is > 0)
            command.Append(" timeout=").Append(timeout.Value);
        return command.ToString();
    }

    private static void Append(StringBuilder command, string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;
        command.Append(' ').Append(key).Append('=').Append(CommandParser.QuoteArg(value));
    }
}
