using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace HistoryJanus.Git;

/// <summary>
/// AI 工作树状态机：纯函数，不访问 Git、不读写文件。
/// 合法正向：active → ready → verified → approved → merged → cleaned；
/// active/ready 可转入 failed/abandoned；后续状态必要时可转入 failed；
/// merged/abandoned 清理失败转入 cleanupFailed（保留现场，不得报已清空）。
/// </summary>
public static class AiWorktreeStateMachine
{
    private static readonly Regex BranchPattern = new(
        @"^ai/(?<project>[^/\\]+)/(?<shortSha>[0-9a-fA-F]{7,40})-(?<seq>[1-9][0-9]*)-(?<slug>[a-z0-9-]+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SlugPattern = new(
        @"^[a-z0-9-]+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ShortShaPattern = new(
        @"^[0-9a-fA-F]{7,40}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool TryTransition(
        AiWorktreeStatus current,
        AiWorktreeStatus next,
        string currentHeadSha,
        string? boundHeadSha,
        out string error)
    {
        error = "";
        if (!IsLegalMove(current, next))
        {
            error = $"不允许从 {FormatStatus(current)} 转移到 {FormatStatus(next)}";
            return false;
        }

        if (RequiresFullHeadBind(next))
        {
            if (!IsFullHeadSha(currentHeadSha))
            {
                error = $"{FormatStatus(next)} 必须绑定完整 HEAD SHA";
                return false;
            }

            if (next != AiWorktreeStatus.Ready)
            {
                if (!IsFullHeadSha(boundHeadSha))
                {
                    error = $"{FormatStatus(next)} 必须绑定完整 HEAD SHA";
                    return false;
                }

                if (!SameSha(currentHeadSha, boundHeadSha!))
                {
                    error = $"HEAD 已变化，{FormatStatus(current)} 绑定的 SHA 已失效";
                    return false;
                }
            }
        }

        return true;
    }

    public static bool IsLegalMove(AiWorktreeStatus current, AiWorktreeStatus next) => (current, next) switch
    {
        (AiWorktreeStatus.Active, AiWorktreeStatus.Ready) => true,
        (AiWorktreeStatus.Active, AiWorktreeStatus.Failed) => true,
        (AiWorktreeStatus.Active, AiWorktreeStatus.Abandoned) => true,
        (AiWorktreeStatus.Ready, AiWorktreeStatus.Verified) => true,
        (AiWorktreeStatus.Ready, AiWorktreeStatus.Failed) => true,
        (AiWorktreeStatus.Ready, AiWorktreeStatus.Abandoned) => true,
        (AiWorktreeStatus.Verified, AiWorktreeStatus.Approved) => true,
        (AiWorktreeStatus.Verified, AiWorktreeStatus.Failed) => true,
        (AiWorktreeStatus.Approved, AiWorktreeStatus.Merged) => true,
        (AiWorktreeStatus.Approved, AiWorktreeStatus.Failed) => true,
        (AiWorktreeStatus.Merged, AiWorktreeStatus.Cleaned) => true,
        (AiWorktreeStatus.Merged, AiWorktreeStatus.CleanupFailed) => true,
        (AiWorktreeStatus.Abandoned, AiWorktreeStatus.Cleaned) => true,
        (AiWorktreeStatus.Abandoned, AiWorktreeStatus.CleanupFailed) => true,
        _ => false,
    };

    /// <summary>verified / approved / merged 以及锁定点 ready 必须绑定完整 HEAD。</summary>
    public static bool RequiresFullHeadBind(AiWorktreeStatus next) => next is
        AiWorktreeStatus.Ready or
        AiWorktreeStatus.Verified or
        AiWorktreeStatus.Approved or
        AiWorktreeStatus.Merged;

    public static bool IsFullHeadSha(string? sha)
    {
        if (string.IsNullOrWhiteSpace(sha))
            return false;
        var value = sha.Trim();
        if (value.Length is not (40 or 64))
            return false;
        foreach (var c in value)
        {
            if (!char.IsAsciiHexDigit(c))
                return false;
        }
        return true;
    }

    public static string FormatStatus(AiWorktreeStatus status) => status switch
    {
        AiWorktreeStatus.Active => "active",
        AiWorktreeStatus.Ready => "ready",
        AiWorktreeStatus.Verified => "verified",
        AiWorktreeStatus.Approved => "approved",
        AiWorktreeStatus.Merged => "merged",
        AiWorktreeStatus.Cleaned => "cleaned",
        AiWorktreeStatus.Failed => "failed",
        AiWorktreeStatus.Abandoned => "abandoned",
        AiWorktreeStatus.CleanupFailed => "cleanupFailed",
        _ => status.ToString(),
    };

    public static bool TryParseStatus(string? text, out AiWorktreeStatus status)
    {
        status = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        switch (text.Trim())
        {
            case "active": status = AiWorktreeStatus.Active; return true;
            case "ready": status = AiWorktreeStatus.Ready; return true;
            case "verified": status = AiWorktreeStatus.Verified; return true;
            case "approved": status = AiWorktreeStatus.Approved; return true;
            case "merged": status = AiWorktreeStatus.Merged; return true;
            case "cleaned": status = AiWorktreeStatus.Cleaned; return true;
            case "failed": status = AiWorktreeStatus.Failed; return true;
            case "abandoned": status = AiWorktreeStatus.Abandoned; return true;
            case "cleanupFailed": status = AiWorktreeStatus.CleanupFailed; return true;
            default: return false;
        }
    }

    public static bool TryValidateSlug([NotNullWhen(true)] string? slug, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(slug))
        {
            error = "slug 不能为空";
            return false;
        }

        var value = slug.Trim();
        if (!SlugPattern.IsMatch(value))
        {
            error = "slug 只允许小写字母、数字和连字符";
            return false;
        }

        return true;
    }

    public static bool TryParseBranchName(
        [NotNullWhen(true)] string? branchName,
        [NotNullWhen(true)] out AiWorktreeBranchName? parsed,
        out string error)
    {
        parsed = null;
        error = "";
        if (string.IsNullOrWhiteSpace(branchName))
        {
            error = "AI 分支名不能为空";
            return false;
        }

        var value = branchName.Trim();
        var match = BranchPattern.Match(value);
        if (!match.Success)
        {
            error = "AI 分支名必须是 ai/<项目>/<短SHA>-<序号>-<slug>";
            return false;
        }

        var project = match.Groups["project"].Value;
        var shortSha = match.Groups["shortSha"].Value;
        var sequence = int.Parse(match.Groups["seq"].Value, CultureInfo.InvariantCulture);
        var slug = match.Groups["slug"].Value;
        if (!TryValidateSlug(slug, out error))
            return false;

        parsed = new AiWorktreeBranchName(project, shortSha, sequence, slug);
        return true;
    }

    /// <summary>工作树目录名 <c>{shortSha}-{n}-{slug}</c>，禁止斜杠。</summary>
    public static bool TryFormatWorktreeFolderName(
        string? shortSha,
        int sequence,
        string? slug,
        out string folderName,
        out string error)
    {
        folderName = "";
        error = "";
        if (string.IsNullOrWhiteSpace(shortSha) || !ShortShaPattern.IsMatch(shortSha.Trim()))
        {
            error = "短 SHA 必须是 7 到 40 位十六进制";
            return false;
        }

        if (sequence < 1)
        {
            error = "序号必须是正整数";
            return false;
        }

        if (!TryValidateSlug(slug, out error))
            return false;

        var slugValue = slug.Trim();
        folderName = shortSha.Trim()
            + "-" + sequence.ToString(CultureInfo.InvariantCulture)
            + "-" + slugValue;
        if (folderName.Contains('/') || folderName.Contains('\\'))
        {
            folderName = "";
            error = "工作树目录名不能包含斜杠";
            return false;
        }

        return true;
    }

    private static bool SameSha(string left, string right)
        => left.Trim().Equals(right.Trim(), StringComparison.OrdinalIgnoreCase);
}
