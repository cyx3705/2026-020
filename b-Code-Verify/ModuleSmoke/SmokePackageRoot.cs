using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

/// <summary>将管线传入的单个构建输出包装为隔离运行区；正式候选输入仍由宿主原样校验。</summary>
internal sealed class SmokePackageRoot : IDisposable
{
    private readonly string? _temporary;
    public string Root { get; }

    public SmokePackageRoot(string input)
    {
        var manifestPath = Path.Combine(input, "module.manifest.json");
        if (!File.Exists(manifestPath))
        {
            Root = input;
            return;
        }

        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (manifest.RootElement.GetProperty("name").GetString() != "HistoryJanus"
            || manifest.RootElement.GetProperty("artifact").GetString() != "HistoryJanus.dll")
            throw new InvalidOperationException("ModuleSmoke expects a HistoryJanus package");
        _temporary = Path.Combine(Path.GetTempPath(), "HistoryJanus-ModuleSmoke", Guid.NewGuid().ToString("N"));
        Root = _temporary;
        var package = Path.Combine(Root, "HistoryJanus");
        Directory.CreateDirectory(package);
        // 管线的 bin 是验证输入，不是发布包：仅复制本模块的声明载荷，不带宿主私有副本。
        foreach (var name in new[] { "HistoryJanus.dll", "HistoryJanus.xml", "module.manifest.json" })
            File.Copy(Path.Combine(input, name), Path.Combine(package, name));
        var checksum = Path.Combine(input, "SHA256SUMS");
        if (File.Exists(checksum))
        {
            // 完整候选不重算校验和，损坏候选必须被正式发现器拒绝。
            foreach (var file in Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(package, Path.GetRelativePath(input, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
            }
        }
        else
        {
            var sums = Directory.EnumerateFiles(package).OrderBy(Path.GetFileName, StringComparer.Ordinal)
                .Select(file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))) + "  " + Path.GetFileName(file)).ToArray();
            File.WriteAllLines(Path.Combine(package, "SHA256SUMS"), sums);
        }
    }

    public void Dispose()
    {
        if (_temporary != null && Directory.Exists(_temporary))
            Directory.Delete(_temporary, true);
    }
}
