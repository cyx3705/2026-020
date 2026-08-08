using System.IO;

namespace HistoryJanus.Git;

public enum GitlinkKind
{
    StandardSubmodule,
    LegacyGitlink,
}

public enum SubmoduleOperationOutcome
{
    Skipped,
    Success,
    Rejected,
    Failed,
}

public enum RepositoryTarget
{
    Parent,
    Submodules,
    Both,
}

public sealed record GitlinkDescriptor(
    string RelativePath,
    string FullPath,
    GitlinkKind Kind,
    string Name,
    string IndexSha,
    string HeadSha,
    string Branch,
    bool IsDirty,
    int ModifiedCount,
    int DeletedCount,
    int UntrackedCount,
    bool NestedGitlinkDirty,
    bool HasIdentity,
    bool HasOrigin);

public sealed record SubmoduleOperationEntry(
    string RelativePath,
    string Branch,
    string BeforeSha,
    string AfterSha,
    SubmoduleOperationOutcome Outcome,
    string Message,
    GitlinkKind Kind,
    bool Pushed = false);

public sealed record PushReport(
    bool Success,
    string Message,
    bool ParentPushed = false,
    IReadOnlyList<SubmoduleOperationEntry>? Submodules = null,
    bool PartialCompletion = false,
    RepositoryTarget Target = RepositoryTarget.Parent,
    bool ParentPointerPending = false);

public sealed record ProjectCommitResult(string Project, CommitReport Report);

public sealed record BatchCommitReport(
    bool Success,
    string Message,
    IReadOnlyList<ProjectCommitResult> Projects,
    bool PartialCompletion = false,
    RepositoryTarget Target = RepositoryTarget.Parent,
    int ParentPointerPendingCount = 0);

public sealed record BatchPushReport(
    bool Success,
    string Message,
    bool ParentPushed,
    IReadOnlyList<SubmoduleOperationEntry> Submodules,
    bool PartialCompletion = false,
    RepositoryTarget Target = RepositoryTarget.Parent,
    int ParentPointerPendingCount = 0);

/// <summary>直属 160000 gitlink 的结构化发现与只读状态校验。</summary>
public sealed class GitlinkService
{
    public async Task<(bool Success, string Message, List<GitlinkDescriptor> Items)> DiscoverAsync(
        string parentPath, CancellationToken cancellation = default)
    {
        var listed = await GitRunner.RunAsync(parentPath, ["ls-files", "--stage", "-z"],
            cancellation: cancellation);
        if (!listed.Success)
            return (false, $"读取 gitlink 索引失败:\n{listed.Output}", []);

        var entries = ParseStageEntries(listed.Output)
            .Where(entry => entry.Mode == "160000")
            .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (entries.Count == 0)
            return (true, "未发现直属子模块", []);

        var standardNames = await ReadStandardSubmoduleNamesAsync(parentPath, cancellation);
        var items = new List<GitlinkDescriptor>(entries.Count);
        foreach (var entry in entries)
        {
            var pathCheck = ValidateChildPath(parentPath, entry.Path);
            if (!pathCheck.Success)
                return (false, pathCheck.Message, []);
            var fullPath = pathCheck.FullPath!;

            if (!Directory.Exists(fullPath))
                return (false, $"子模块未初始化或目录缺失: {entry.Path}", []);
            if (ContainsReparsePoint(parentPath, fullPath))
                return (false, $"子模块路径包含重解析点，已拒绝越界风险: {entry.Path}", []);

            var inside = await GitRunner.RunAsync(fullPath,
                ["rev-parse", "--is-inside-work-tree"], cancellation: cancellation);
            if (!inside.Success || !inside.Output.Split('\n')[0].Trim()
                    .Equals("true", StringComparison.OrdinalIgnoreCase))
                return (false, $"gitlink 不是有效 Git 工作树: {entry.Path}", []);

            var top = await GitRunner.RunAsync(fullPath, ["rev-parse", "--show-toplevel"],
                cancellation: cancellation);
            if (!top.Success || !PathsEqual(top.Output.Split('\n')[0].Trim(), fullPath))
                return (false, $"gitlink 工作树根与登记路径不一致: {entry.Path}", []);

            var head = await GitRunner.RunAsync(fullPath, ["rev-parse", "HEAD"],
                cancellation: cancellation);
            if (!head.Success)
                return (false, $"无法读取子模块 HEAD: {entry.Path}\n{head.Output}", []);
            var headSha = FirstLine(head.Output);

            var branchResult = await GitRunner.RunAsync(fullPath,
                ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellation: cancellation);
            var branch = branchResult.Success ? FirstLine(branchResult.Output) : string.Empty;

            var statusResult = await GitRunner.RunAsync(fullPath,
                ["status", "--porcelain=v1", "-z", "--untracked-files=all"],
                cancellation: cancellation);
            if (!statusResult.Success)
                return (false, $"读取子模块状态失败: {entry.Path}\n{statusResult.Output}", []);
            var statuses = ParsePorcelain(statusResult.Output);

            var nestedResult = await GitRunner.RunAsync(fullPath,
                ["ls-files", "--stage", "-z"], cancellation: cancellation);
            if (!nestedResult.Success)
                return (false, $"读取嵌套 gitlink 失败: {entry.Path}\n{nestedResult.Output}", []);
            var nestedPaths = ParseStageEntries(nestedResult.Output)
                .Where(item => item.Mode == "160000")
                .Select(item => NormalizeGitPath(item.Path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var nestedDirty = statuses.Any(status => nestedPaths.Contains(status.Path));

            var nameResult = await GitRunner.RunAsync(fullPath, ["config", "--get", "user.name"],
                cancellation: cancellation);
            var emailResult = await GitRunner.RunAsync(fullPath, ["config", "--get", "user.email"],
                cancellation: cancellation);
            var originResult = await GitRunner.RunAsync(fullPath,
                ["remote", "get-url", "origin"], cancellation: cancellation);

            CountStatuses(statuses, out var modified, out var deleted, out var untracked);
            var normalizedPath = NormalizeGitPath(entry.Path);
            var isStandard = standardNames.TryGetValue(normalizedPath, out var declaredName);
            items.Add(new GitlinkDescriptor(
                normalizedPath,
                fullPath,
                isStandard ? GitlinkKind.StandardSubmodule : GitlinkKind.LegacyGitlink,
                isStandard ? declaredName! : Path.GetFileName(fullPath),
                entry.Sha,
                headSha,
                branch,
                statuses.Count > 0,
                modified,
                deleted,
                untracked,
                nestedDirty,
                nameResult.Success && !string.IsNullOrWhiteSpace(FirstLine(nameResult.Output))
                                   && emailResult.Success && !string.IsNullOrWhiteSpace(FirstLine(emailResult.Output)),
                originResult.Success && !string.IsNullOrWhiteSpace(FirstLine(originResult.Output))));
        }

        return (true, $"发现 {items.Count} 个直属子模块", items);
    }

    public async Task<(bool Success, string Message, string Sha)> GetHeadGitlinkShaAsync(
        string parentPath, string relativePath, CancellationToken cancellation = default)
    {
        var result = await GitRunner.RunAsync(parentPath,
            ["ls-tree", "-z", "HEAD", "--", relativePath], cancellation: cancellation);
        if (!result.Success)
            return (false, $"读取父提交 gitlink 失败: {relativePath}\n{result.Output}", string.Empty);
        var entry = ParseTreeEntry(result.Output);
        if (entry == null || entry.Value.Mode != "160000")
            return (false, $"父提交未记录有效 gitlink: {relativePath}", string.Empty);
        return (true, string.Empty, entry.Value.Sha);
    }

    public static (bool Success, string Message, string? FullPath) ValidateChildPath(
        string parentPath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            return (false, $"非法 gitlink 路径: {relativePath}", null);
        try
        {
            var parent = Path.GetFullPath(parentPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(parent, relativePath));
            var prefix = parent + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return (false, $"gitlink 路径越出父项目: {relativePath}", null);
            return (true, string.Empty, full);
        }
        catch (Exception ex)
        {
            return (false, $"无法解析 gitlink 路径 {relativePath}: {ex.Message}", null);
        }
    }

    private static async Task<Dictionary<string, string>> ReadStandardSubmoduleNamesAsync(
        string parentPath, CancellationToken cancellation)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(Path.Combine(parentPath, ".gitmodules")))
            return result;
        var config = await GitRunner.RunAsync(parentPath,
            ["config", "-f", ".gitmodules", "--get-regexp", "^submodule\\..*\\.path$"],
            cancellation: cancellation);
        if (!config.Success)
            return result;
        foreach (var line in config.Output.Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            const string marker = ".path ";
            var markerIndex = line.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex <= 0 || markerIndex + marker.Length >= line.Length)
                continue;
            var key = line[..(markerIndex + ".path".Length)].Trim();
            var path = NormalizeGitPath(line[(markerIndex + marker.Length)..].Trim());
            const string prefix = "submodule.";
            const string suffix = ".path";
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                result[path] = key[prefix.Length..^suffix.Length];
        }
        return result;
    }

    private static List<(string Code, string Path)> ParsePorcelain(string output)
    {
        var result = new List<(string Code, string Path)>();
        var records = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < records.Length; i++)
        {
            var record = records[i];
            if (record.Length < 4)
                continue;
            var code = record[..2];
            result.Add((code, NormalizeGitPath(record[3..])));
            if ((code.Contains('R') || code.Contains('C')) && i + 1 < records.Length)
                i++;
        }
        return result;
    }

    private static void CountStatuses(
        IEnumerable<(string Code, string Path)> statuses,
        out int modified, out int deleted, out int untracked)
    {
        modified = deleted = untracked = 0;
        foreach (var status in statuses)
        {
            if (status.Code == "??")
                untracked++;
            else if (status.Code.Contains('D'))
                deleted++;
            else
                modified++;
        }
    }

    private static List<(string Mode, string Sha, string Path)> ParseStageEntries(string output)
    {
        var result = new List<(string Mode, string Sha, string Path)>();
        foreach (var record in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = record.IndexOf('\t');
            if (tab < 0)
                continue;
            var fields = record[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 3)
                result.Add((fields[0], fields[1], record[(tab + 1)..]));
        }
        return result;
    }

    private static (string Mode, string Sha, string Path)? ParseTreeEntry(string output)
    {
        var record = output.Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (record == null)
            return null;
        var tab = record.IndexOf('\t');
        if (tab < 0)
            return null;
        var fields = record[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 3 ? (fields[0], fields[2], record[(tab + 1)..]) : null;
    }

    private static bool ContainsReparsePoint(string parentPath, string fullPath)
    {
        var parent = Path.GetFullPath(parentPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var relative = Path.GetRelativePath(parent, fullPath);
        var current = parent;
        foreach (var part in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return true;
        }
        return false;
    }

    private static bool PathsEqual(string left, string right)
        => Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Equals(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);

    private static string NormalizeGitPath(string path) => path.Replace('\\', '/');

    private static string FirstLine(string value)
        => value.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
}
