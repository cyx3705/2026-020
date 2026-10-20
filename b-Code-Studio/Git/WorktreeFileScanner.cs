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
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                // 目录枚举能看见、Win32 却打不开的名字（末尾的点、空格）会让 Length 抛
                // FileNotFoundException。这种条目没有可提交的内容，不能把整次提交打断。
                if (!TryGetLength(file, out var length))
                    continue;
                if (length >= rejectBytes)
                {
                    status = FileSizeCheckStatus.Rejected;
                    largeFiles.Add(new LargeFileEntry(GetRelativePath(rootPath, file), length));
                }
                else if (length >= warnBytes)
                {
                    if (status != FileSizeCheckStatus.Rejected)
                        status = FileSizeCheckStatus.Warning;
                    largeFiles.Add(new LargeFileEntry(GetRelativePath(rootPath, file), length));
                }
            }

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                var fullChild = Path.GetFullPath(child)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (Path.GetFileName(fullChild).Equals(".git", StringComparison.OrdinalIgnoreCase)
                    || excluded.Any(path => fullChild.Equals(path, StringComparison.OrdinalIgnoreCase)
                                            || fullChild.StartsWith(path + Path.DirectorySeparatorChar,
                                                StringComparison.OrdinalIgnoreCase))
                    || !TryGetAttributes(fullChild, out var attributes)
                    || (attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                pending.Push(fullChild);
            }
        }

        return (status, largeFiles.OrderByDescending(f => f.SizeBytes).ToList());
    }

    private static bool TryGetLength(string file, out long length)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists)
            {
                length = 0;
                return false;
            }
            length = info.Length;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            length = 0;
            return false;
        }
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            attributes = 0;
            return false;
        }
    }

    private static string GetRelativePath(string rootPath, string filePath)
    {
        var relative = Path.GetRelativePath(rootPath, filePath);
        return relative.Replace('\\', '/');
    }
}
