using System.IO;
using System.Text;

namespace HistoryJanus.Git;

// worktree LFS 操作统一经 GitRunner 封装。

public static class WorktreeLfsHelper
{
    private const string LfsPointerVersion = "version https://git-lfs.github.com/spec/v1";

    public static bool IsLfsPointerFile(string absolutePath)
    {
        try
        {
            var info = new FileInfo(absolutePath);
            if (!info.Exists || info.Length > 2048)
                return false;

            using var reader = new StreamReader(absolutePath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadLine()?.Trim() == LfsPointerVersion;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> IsFileManagedByLfsAsync(string worktreePath, string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var absolutePath = Path.Combine(worktreePath, normalized.Replace('/', Path.DirectorySeparatorChar));

        if (IsLfsPointerFile(absolutePath))
            return true;

        var attrResult = await GitRunner.RunAsync(worktreePath, ["check-attr", "filter", "--", normalized]);
        if (attrResult.Success
            && attrResult.Output.Contains("filter: lfs", StringComparison.OrdinalIgnoreCase))
            return true;

        var lfsResult = await GitRunner.RunAsync(worktreePath, ["lfs", "ls-files", "-n"]);
        if (lfsResult.Success && lfsResult.Output.Replace("\r\n", "\n")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(path => path.Replace('\\', '/').Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }

    public static async Task<bool> IsGitLfsAvailableAsync(string worktreePath)
    {
        var result = await GitRunner.RunAsync(worktreePath, ["lfs", "version"]);
        return result.Success && result.Output.Contains("git-lfs", StringComparison.OrdinalIgnoreCase);
    }
}
