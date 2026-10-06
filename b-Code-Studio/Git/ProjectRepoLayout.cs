using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HistoryJanus.Git;

/// <summary>
/// 一项目一仓的目录约定、工作树拷贝与清单字段。
/// 不发 git 进程；身份规则与路径变换集中在此，避免撑破 ProjectService 行数上限。
/// </summary>
internal static class ProjectRepoLayout
{
    public const string DefaultLibraryRoot = @"C:\OneHistory\HistoryClio";
    public const string MainlineBranch = "main";
    public const string DefaultTemplate = "0000-000-Template";

    private static readonly Regex NumberedName =
        new(@"^\d{4}-\d{3}-.+", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ProjectNumber =
        new(@"^(?<id>\d{4}-\d{3})(?:-|$)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsRegisteredProjectName(string name)
        => name.Length > 0
           && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
           && (name.StartsWith("0000-", StringComparison.Ordinal) || NumberedName.IsMatch(name));

    /// <summary>
    /// 远端仓库名取项目编号 <c>YYYY-NNN</c>，不带中文标题部分。
    /// 本地目录保留中文是为了人肉查找；而 GitHub 会把非 ASCII 字符整段吃掉
    /// （请求 <c>2025-001-AGV洗轮机</c> 建出来的是 <c>2025-001-AGV-</c>），
    /// 编号本身已经唯一，用它既避免被截断成无意义的名字，也不会互相撞名。
    /// 没有编号前缀的项目名原样返回，由调用方按 GitHub 命名规则校验。
    /// </summary>
    public static string ToRemoteRepositoryName(string projectName)
    {
        var trimmed = (projectName ?? string.Empty).Trim();
        var match = ProjectNumber.Match(trimmed);
        return match.Success ? match.Groups["id"].Value : trimmed;
    }

    public static bool IsIndependentGitRepo(string projectPath)
        => Directory.Exists(Path.Combine(projectPath, ".git"));

    public static bool IsGitPointerFile(string projectPath)
        => File.Exists(Path.Combine(projectPath, ".git"))
           && !Directory.Exists(Path.Combine(projectPath, ".git"));

    public static string ReadGitPointer(string projectPath)
    {
        var git = Path.Combine(projectPath, ".git");
        try
        {
            return File.Exists(git) && !Directory.Exists(git)
                ? File.ReadAllText(git).Trim()
                : "";
        }
        catch (IOException)
        {
            return "";
        }
        catch (UnauthorizedAccessException)
        {
            return "";
        }
    }

    public static string? ReadTemplateSource(string projectPath)
    {
        var path = Path.Combine(projectPath, "project.manifest.json");
        if (!File.Exists(path))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("template", out var template)
                && template.TryGetProperty("source", out var source))
            {
                var value = source.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }

        return null;
    }

    public static void StampNewProjectManifest(string projectPath, string templateName)
    {
        var path = Path.Combine(projectPath, "project.manifest.json");
        if (!File.Exists(path))
            return;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path));
            if (node is not JsonObject root)
                return;
            var template = root["template"] as JsonObject ?? new JsonObject();
            template["isTemplate"] = false;
            template["source"] = templateName;
            root["template"] = template;
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (JsonException)
        {
            // 清单损坏时保留原文件，不阻断建仓。
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// 复制工作树。跳过 <c>.git</c>，也跳过根下以 z/Z 开头的文件夹：
    /// 那是本仓自己的元数据或备用模板，新项目不继承，用到时再从模板仓复制。
    /// 根上名字以 z 开头的文件不是文件夹，照常复制；普通目录里面的 z 段也照常复制。
    /// </summary>
    public static void CopyWorkingTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            if (IsGitSegment(source, directory) || IsUnderTopLevelZFolder(source, directory, isDirectory: true))
                continue;
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                continue;
            Directory.CreateDirectory(Map(source, destination, directory));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (IsGitSegment(source, file) || IsUnderTopLevelZFolder(source, file, isDirectory: false))
                continue;
            var target = Map(source, destination, file);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    public static void CopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source))
            return;
        Directory.CreateDirectory(destination);
        var pending = new Stack<(string Source, string Destination)>();
        pending.Push((source, destination));
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(current.Source))
                File.Copy(file, Path.Combine(current.Destination, Path.GetFileName(file)), overwrite: true);
            foreach (var directory in Directory.EnumerateDirectories(current.Source))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    continue;
                var target = Path.Combine(current.Destination, Path.GetFileName(directory));
                Directory.CreateDirectory(target);
                pending.Push((directory, target));
            }
        }
    }

    public static void DeleteTree(string path)
    {
        if (!Directory.Exists(path))
            return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        Directory.Delete(path, recursive: true);
    }

    private static bool IsGitSegment(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Equals(".git", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 与 ReadZFolderNames 同一口径：只看根下第一段是否以 z/Z 开头。
    /// 目录自身（只有一段）也算；根上的文件只有一段且不是目录，不排除。
    /// </summary>
    private static bool IsUnderTopLevelZFolder(string root, string path, bool isDirectory)
    {
        var relative = Path.GetRelativePath(root, path);
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Length == 0 || parts[0].Length == 0 || parts[0] == ".")
            return false;
        var topIsZ = parts[0][0] == 'z' || parts[0][0] == 'Z';
        if (!topIsZ)
            return false;
        return isDirectory || parts.Length >= 2;
    }

    private static string Map(string source, string destination, string path)
        => Path.Combine(destination, Path.GetRelativePath(source, path));
}
