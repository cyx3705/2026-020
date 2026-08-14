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
    public const string LegacyVestaLibrary = @"C:\OneHistory\HistoryVesta";
    public const string MainlineBranch = "main";
    public const string DefaultTemplate = "0000-000-Template";

    private static readonly Regex NumberedName =
        new(@"^\d{4}-\d{3}-.+", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsRegisteredProjectName(string name)
        => name.Length > 0
           && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
           && (name.StartsWith("0000-", StringComparison.Ordinal) || NumberedName.IsMatch(name));

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

    public static void CopyWorkingTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            if (IsGitSegment(source, directory))
                continue;
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                continue;
            Directory.CreateDirectory(Map(source, destination, directory));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (IsGitSegment(source, file))
                continue;
            var target = Map(source, destination, file);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
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

    private static string Map(string source, string destination, string path)
        => Path.Combine(destination, Path.GetRelativePath(source, path));
}
