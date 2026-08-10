using System.IO;

namespace HistoryJanus.Git;

/// <summary>
/// ProjectService 的 Meta 文件夹切面：项目根下以 z/Z 开头的一级子目录。
/// </summary>
public sealed partial class ProjectService
{
    // ---------------------------------------------------------------- Meta 文件夹

    /// <summary>
    /// 扫描全部 worktree 根下以 z/Z 开头的一级子文件夹。
    /// 单项目枚举失败只计入警告并跳过,不拖垮整次扫描。
    /// </summary>
    public async Task<(GitResult Git, List<MetaFolderInfo> Metas, List<string> Warnings)> ListMetaFoldersAsync()
    {
        var (git, worktrees) = await ListWorktreesAsync();
        if (!git.Success)
            return (git, new List<MetaFolderInfo>(), new List<string>());

        var metas = new List<MetaFolderInfo>();
        var warnings = new List<string>();

        foreach (var w in worktrees)
        {
            if (!Directory.Exists(w.WorktreePath))
                continue;

            try
            {
                foreach (var dir in Directory.EnumerateDirectories(w.WorktreePath))
                {
                    var name = Path.GetFileName(dir);
                    if (name.Length == 0 || (name[0] != 'z' && name[0] != 'Z'))
                        continue;

                    var time = "";
                    try
                    {
                        time = Directory.GetLastWriteTime(dir).ToString("yyyy-MM-dd HH:mm");
                    }
                    catch
                    {
                        // 取时间失败不影响列入
                    }

                    metas.Add(new MetaFolderInfo(w.BranchName, name, dir, time));
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"{w.BranchName}: {ex.Message}");
            }
        }

        metas.Sort((a, b) =>
        {
            var c = string.Compare(a.ProjectName, b.ProjectName, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.Compare(a.MetaName, b.MetaName, StringComparison.OrdinalIgnoreCase);
        });

        return (git, metas, warnings);
    }

    public async Task<(bool Success, string Message)> OpenMetaFolderAsync(
        string? path,
        string? projectName,
        string? metaName)
    {
        string target;
        if (!string.IsNullOrWhiteSpace(path))
        {
            target = path.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(projectName) && !string.IsNullOrWhiteSpace(metaName))
        {
            var (resolved, resolveMessage, worktree) = await ResolveWorktreeAsync(projectName.Trim());
            if (!resolved || worktree == null)
                return (false, resolveMessage);
            target = Path.Combine(worktree.WorktreePath, metaName.Trim());
        }
        else
        {
            return (false, "请提供 path= 或同时提供 name= 与 meta=");
        }

        try
        {
            target = NormalizePath(target);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (false, $"元文件夹路径无效: {ex.Message}");
        }

        if (!Directory.Exists(target))
            return (false, $"目录不存在: {target}");

        var folderName = Path.GetFileName(target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (folderName.Length == 0 || (folderName[0] != 'z' && folderName[0] != 'Z'))
            return (false, $"不是 Z 级元文件夹(名称须以 z/Z 开头): {target}");

        if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
            return (false, $"元文件夹不能是符号链接或目录联接: {target}");

        var parent = Directory.GetParent(target)?.FullName;
        var (git, worktrees) = await ListWorktreesAsync();
        if (!git.Success)
            return (false, $"无法验证元文件夹所属工作树:\n{git.Output}");
        if (parent == null || !worktrees.Any(worktree => PathsEqual(parent, worktree.WorktreePath)))
            return (false, $"目录不是已登记 worktree 的一级元文件夹: {target}");

        System.Diagnostics.Process.Start("explorer.exe", target);
        return (true, $"已在资源管理器中打开: {target}");
    }
}
