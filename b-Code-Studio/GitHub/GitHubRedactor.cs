using System.Text.RegularExpressions;

namespace HistoryJanus.GitHub;

public static partial class GitHubRedactor
{
    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? "";
        var redacted = PrivateKeyBlock().Replace(value, "[REDACTED PRIVATE KEY]");
        redacted = Token().Replace(redacted, "[REDACTED TOKEN]");
        redacted = SecretAssignment().Replace(redacted, "$1=[REDACTED]");
        redacted = Bearer().Replace(redacted, "$1[REDACTED]");
        return UrlUserInfo().Replace(redacted, "$1[REDACTED]@");
    }

    public static string SanitizeRemoteUrl(string? value)
    {
        var redacted = Redact(value).Trim();
        if (!Uri.TryCreate(redacted, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.UserInfo))
            return redacted;
        var builder = new UriBuilder(uri) { UserName = "", Password = "" };
        return builder.Uri.ToString();
    }

    [GeneratedRegex("-----BEGIN [^-]*PRIVATE KEY-----[\\s\\S]*?-----END [^-]*PRIVATE KEY-----", RegexOptions.IgnoreCase)]
    private static partial Regex PrivateKeyBlock();

    [GeneratedRegex("(?:github_pat_[A-Za-z0-9_]+|gh[pousr]_[A-Za-z0-9_]+)", RegexOptions.IgnoreCase)]
    private static partial Regex Token();

    [GeneratedRegex("(?i)\\b(token|password|secret|client_secret)\\s*[=:]\\s*[^\\s;]+")]
    private static partial Regex SecretAssignment();

    [GeneratedRegex("(?i)(authorization\\s*:\\s*bearer\\s+)[^\\s]+")]
    private static partial Regex Bearer();

    [GeneratedRegex("(https?://)[^/@\\s]+@", RegexOptions.IgnoreCase)]
    private static partial Regex UrlUserInfo();
}
