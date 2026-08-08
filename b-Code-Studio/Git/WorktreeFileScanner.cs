using System.IO;

namespace HistoryJanus.Git;

// worktree 文件扫描器。
// 阈值由调用方从 proj.warnmb / proj.rejectmb 配置传入。

public enum FileSizeCheckStatus
{
    Ok,
    Warning,
    Rejected,
}

public record LargeFileEntry(string RelativePath, long SizeBytes)
{
    public string FormattedSize => FormatBytes(SizeBytes);

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        if (bytes >= 1024 * 1024)
            return $"{bytes / (1024.0 * 1024):F2} MB";
        if (bytes >= 1024)
            return $"{bytes / 1024.0:F2} KB";
        return $"{bytes} B";
    }
}

public static class WorktreeFileScanner
{
    public static (FileSizeCheckStatus Status, List<LargeFileEntry> LargeFiles) ScanDirectory(
        string rootPath, long warnBytes, long rejectBytes,
        IEnumerable<string>? excludedDirectories = null)
    {
        var largeFiles = new List<LargeFileEntry>();
        var status = FileSizeCheckStatus.Ok;

        if (!Directory.Exists(rootPath))
            return (FileSizeCheckStatus.Rejected, largeFiles);

        var excluded = (excludedDirectories ?? [])
            .Select(path => Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .ToList();

        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(rootPath));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                var info = new FileInfo(file);
                if (info.Length >= rejectBytes)
                {
                    status = FileSizeCheckStatus.Rejected;
                    largeFiles.Add(new LargeFileEntry(GetRelativePath(rootPath, file), info.Length));
                }
                else if (info.Length >= warnBytes)
                {
                    if (status != FileSizeCheckStatus.Rejected)
                        status = FileSizeCheckStatus.Warning;
                    largeFiles.Add(new LargeFileEntry(GetRelativePath(rootPath, file), info.Length));
                }
            }

            foreach (var child in Directory.EnumerateDirectories(
                         directory, "*", SearchOption.TopDirectoryOnly))
            {
                var fullChild = Path.GetFullPath(child)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (Path.GetFileName(fullChild).Equals(".git", StringComparison.OrdinalIgnoreCase)
                    || excluded.Any(path => fullChild.Equals(path, StringComparison.OrdinalIgnoreCase)
                                            || fullChild.StartsWith(path + Path.DirectorySeparatorChar,
                                                StringComparison.OrdinalIgnoreCase))
                    || (File.GetAttributes(fullChild) & FileAttributes.ReparsePoint) != 0)
                    continue;
                pending.Push(fullChild);
            }
        }

        return (status, largeFiles.OrderByDescending(f => f.SizeBytes).ToList());
    }

    private static string GetRelativePath(string rootPath, string filePath)
    {
        var relative = Path.GetRelativePath(rootPath, filePath);
        return relative.Replace('\\', '/');
    }
}
