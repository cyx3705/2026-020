using System.IO;
using System.Text;

namespace HistoryJanus.Git;

/// <summary>
/// 逐仓诊断：独立 .git、能 status。指向旧裸仓的 .git 文件只报告，不删盘。
/// </summary>
public sealed partial class ProjectService
{
    public async Task<(bool Success, string Message)> RepairAsync(IProgress<string>? progress)
    {
        if (!TryValidateWorktreeRoot(out var rootError))
            return (false, $"库根不安全: {rootError}");

        var issues = new List<string>();
        var ok = 0;
        foreach (var dir in Directory.EnumerateDirectories(LibraryRoot))
        {
            var name = Path.GetFileName(dir);
            if (!ProjectRepoLayout.IsRegisteredProjectName(name))
                continue;
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
            {
                issues.Add($"{name}: 是符号链接或目录联接");
                continue;
            }

            progress?.Report($"检查 {name} ...");
            if (ProjectRepoLayout.IsGitPointerFile(dir))
            {
                var pointer = ProjectRepoLayout.ReadGitPointer(dir);
                issues.Add($"{name}: .git 是文件（可能仍指向旧裸仓）: {pointer}");
                continue;
            }

            if (!ProjectRepoLayout.IsIndependentGitRepo(dir))
            {
                issues.Add($"{name}: 缺少独立 .git 目录");
                continue;
            }

            if (!TryValidateManagedDirectChild(dir, rejectReparsePoint: true, out var pathError))
            {
                issues.Add($"{name}: {pathError}");
                continue;
            }

            var status = await GitRunner.RunAsync(dir, ["status", "--porcelain"]);
            if (!status.Success)
            {
                issues.Add($"{name}: git status 失败: {status.Output.Trim()}");
                continue;
            }

            ok++;
        }

        var sb = new StringBuilder();
        sb.Append($"逐仓诊断完成: {ok} 个独立仓可 status");
        if (issues.Count > 0)
        {
            sb.Append($"\n✗ {issues.Count} 个问题（未删除任何目录）:");
            foreach (var issue in issues)
                sb.Append($"\n  - {issue}");
        }
        else
        {
            sb.Append("\n✓ 未发现指向旧裸仓的 .git 文件");
        }

        return (issues.Count == 0, sb.ToString());
    }
}
