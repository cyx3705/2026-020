using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

/// <summary>把管线传入的构建输出打成 nupkg；输入本身已是 nupkg 时原样交给宿主 --probe。</summary>
internal sealed class SmokePackageRoot : IDisposable
{
    private readonly string? _temporary;
    public string Root { get; }

    /// <summary>交给宿主 --probe 的 nupkg 绝对路径。</summary>
    public string Package { get; }

    public SmokePackageRoot(string input)
    {
        var existing = FindNupkg(input);
        if (existing != null)
        {
            Package = existing;
            Root = Path.GetDirectoryName(existing)!;
            return;
        }

        var manifestPath = Path.Combine(input, "module.manifest.json");
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException($"ModuleSmoke 找不到 nupkg，也找不到 module.manifest.json: {input}");

        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (manifest.RootElement.GetProperty("name").GetString() != "HistoryJanus"
            || manifest.RootElement.GetProperty("artifact").GetString() != "HistoryJanus.dll")
            throw new InvalidOperationException("ModuleSmoke expects a HistoryJanus package");
        var version = manifest.RootElement.GetProperty("version").GetString();
        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidOperationException("ModuleSmoke expects a version");

        _temporary = Path.Combine(Path.GetTempPath(), "HistoryJanus-ModuleSmoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temporary);
        Root = _temporary;
        Package = Path.Combine(_temporary, "HistoryJanus.nupkg");
        Pack(Package, version, input);
    }

    private static string? FindNupkg(string input)
    {
        if (File.Exists(input) && input.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(input);
        if (!Directory.Exists(input))
            return null;
        var found = Directory.GetFiles(input, "*.nupkg", SearchOption.TopDirectoryOnly);
        return found.Length == 1 ? Path.GetFullPath(found[0]) : null;
    }

    private static void Pack(string nupkgPath, string version, string input)
    {
        using var archive = ZipFile.Open(nupkgPath, ZipArchiveMode.Create);
        Write(archive, "HistoryJanus.nuspec", Encoding.UTF8.GetBytes(Nuspec(version)));
        foreach (var name in new[] { "HistoryJanus.dll", "HistoryJanus.xml", "module.manifest.json" })
        {
            var source = Path.Combine(input, name);
            if (!File.Exists(source))
                throw new InvalidOperationException($"构建输出缺少 {name}");
            Write(archive, name, File.ReadAllBytes(source));
        }
    }

    private static string Nuspec(string version)
        => $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>HistoryJanus</id>
                <version>{version}</version>
                <authors>HistoryVulcan</authors>
                <description>HistoryJanus</description>
              </metadata>
            </package>
            """;

    private static void Write(ZipArchive archive, string name, byte[] bytes)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    public void Dispose()
    {
        if (_temporary != null && Directory.Exists(_temporary))
            Directory.Delete(_temporary, true);
    }
}
