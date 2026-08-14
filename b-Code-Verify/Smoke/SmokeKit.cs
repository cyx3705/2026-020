using HistoryVulcan.Core;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryJanus.Git;

namespace HistoryJanus.Smoke;

/// <summary>Smoke 套件共享的断言、临时目录与 Git 助手。</summary>
internal static class SmokeKit
{
    /// <summary>020 项目根。</summary>
    public static string ParentDir { get; } = DiscoverUmbrellaRoot();

    /// <summary>Janus 产品根。</summary>
    public static string RepoRoot { get; } = Path.Combine(ParentDir, "b-Code-Studio");

    /// <summary>Janus 验证组件根。</summary>
    public static string VerifyRoot { get; } = Path.Combine(ParentDir, "b-Code-Verify");

    public static string TemporaryDirectory(string feature)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ohs-smoke-{feature}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string DiscoverUmbrellaRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "HistoryJanus.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"未能从 {AppContext.BaseDirectory} 向上找到 HistoryJanus.sln");
    }

    // ---------------------------------------------------------------- 断言

    public static void Ensure(GitResult result, string name)
    {
        if (!result.Success)
            throw new InvalidOperationException($"FAIL: {name}\n{result.Output}");
    }

    public static void True(bool condition, string name)
    {
        if (!condition)
            throw new InvalidOperationException($"FAIL: {name}");
    }

    public static void Equal<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"FAIL: {name}; expected={expected}, actual={actual}");
    }

    public static void Contains(string actual, string expected, string name)
        => True(actual.Contains(expected, StringComparison.Ordinal), name);

    public static void BytesEqual(byte[] expected, byte[] actual, string name)
        => True(expected.AsSpan().SequenceEqual(actual), name);

    public static void Throws<T>(Action action, string name) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException($"FAIL: {name}; expected {typeof(T).Name}");
    }

    // ---------------------------------------------------------------- Git 读取助手

    public static GitResult EnsureResult(GitResult result, string name)
    {
        Ensure(result, name);
        return result;
    }

    public static string FirstLine(string value)
        => value.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();

    public static string Run(string repository, IReadOnlyList<string> arguments)
    {
        var result = GitRunner.RunAsync(repository, arguments).GetAwaiter().GetResult();
        Ensure(result, string.Join(' ', arguments));
        return result.Output;
    }

    public static string Subject(string repository)
        => FirstLine(Run(repository, ["log", "-1", "--format=%s"]));

    public static string Sha(string repository)
        => FirstLine(Run(repository, ["rev-parse", "HEAD"]));

    public static string RefSha(string repository, string branch)
        => FirstLine(Run(repository, ["rev-parse", $"refs/heads/{branch}"]));

    public static string GitlinkSha(string repository, string relativePath)
        => FirstLine(Run(repository, ["rev-parse", $"HEAD:{relativePath}"]));

    public static async Task InitStandaloneRepo(string path, string user, string email)
    {
        Directory.CreateDirectory(path);
        Ensure(await GitRunner.RunAsync(path, ["init", "-b", "main"]), $"init {Path.GetFileName(path)}");
        await ConfigureIdentity(path, user, email);
    }

    public static void BindLibrary(
        MemorySettings settings, string libraryRoot, string templateName = "0000-000-Template")
    {
        settings.Set(ProjectService.KeyLibraryRoot, libraryRoot);
        settings.Set(ProjectService.KeyBaseBranch, templateName);
    }

    public static async Task WriteProjectManifest(string path, string source, bool isTemplate = false)
    {
        var json =
            "{\n  \"schemaVersion\": 1,\n  \"template\": { \"isTemplate\": " +
            (isTemplate ? "true" : "false") +
            ", \"source\": \"" + source +
            "\" },\n  \"project\": { \"id\": \"x\", \"name\": \"x\", \"title\": \"x\", \"status\": \"active\", \"version\": \"1.0.0\" }\n}\n";
        await File.WriteAllTextAsync(Path.Combine(path, "project.manifest.json"), json);
    }

    public static async Task ConfigureIdentity(string repository, string user, string email)
    {
        Ensure(await GitRunner.RunAsync(repository, ["config", "user.name", user]), "git user name");
        Ensure(await GitRunner.RunAsync(repository, ["config", "user.email", email]), "git user email");
    }

    public static async Task CommitFile(
        string repository, string relativePath, string content, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(repository, relativePath), content);
        Ensure(await GitRunner.RunAsync(repository, ["add", "--", relativePath]), $"add {relativePath}");
        Ensure(await GitRunner.RunAsync(repository, ["commit", "-m", message]), $"commit {message}");
    }

    // ---------------------------------------------------------------- 清理

    public static void DeleteTree(string path)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                if (!Directory.Exists(path))
                    return;
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(100 * attempt));
            }
        }
    }
}

/// <summary>内存设置服务。</summary>
internal sealed class MemorySettings : ISettingsService
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public string? Get(string key) => _values.GetValueOrDefault(key);
    public int GetInt(string key, int fallback) => int.TryParse(Get(key), out var value) ? value : fallback;
    public void Set(string key, string value) => _values[key] = value;
    public IReadOnlyList<KeyValuePair<string, string>> All() => _values.ToList();
}

/// <summary>内存日志;供需要检查回显与结果日志的用例使用。</summary>
internal sealed class MemoryLog : IShellLog
{
    private readonly List<ShellLogEntry> _entries = [];

    public event EventHandler<ShellLogEntry>? EntryAdded;

    public void Log(ShellLogLevel level, string category, string message)
    {
        var entry = new ShellLogEntry(DateTime.UtcNow, level, category, message);
        _entries.Add(entry);
        EntryAdded?.Invoke(this, entry);
    }

    public IReadOnlyList<ShellLogEntry> Snapshot() => _entries.ToList();
}
