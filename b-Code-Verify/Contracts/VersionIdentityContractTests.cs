using System.Reflection;
using System.Text.Json;
using HistoryJanus.Module;
using Xunit;

namespace HistoryJanus.Contracts;

/// <summary>
/// 版本身份合同：JanusVersion.props 是唯一权威源，程序集 identity、随包 manifest 和
/// 运行时 Status 文本必须全部是从它出发的投影（代码管道化 §4.5），不允许第二份硬编码。
/// </summary>
public sealed class VersionIdentityContractTests
{
    private static readonly Assembly ModuleAssembly = typeof(HistoryJanusCommands).Assembly;

    private static string InformationalVersion
        => ModuleAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
           ?? throw new InvalidOperationException("模块程序集缺少 InformationalVersion");

    [Fact]
    public void AssemblyIdentityAgreesWithTheSingleVersionSource()
    {
        var assemblyVersion = ModuleAssembly.GetName().Version
            ?? throw new InvalidOperationException("模块程序集缺少 AssemblyVersion");
        var fileVersion = ModuleAssembly
            .GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
            ?? throw new InvalidOperationException("模块程序集缺少 FileVersion");

        Assert.Equal($"{InformationalVersion}.0", assemblyVersion.ToString());
        Assert.Equal($"{InformationalVersion}.0", fileVersion);
        Assert.Equal(3, InformationalVersion.Split('.').Length);
    }

    [Fact]
    public void ShippedManifestProjectsTheSameVersion()
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "module.manifest.json");
        Assert.True(File.Exists(manifestPath), $"module.manifest.json 未随测试输出: {manifestPath}");

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var version = document.RootElement.GetProperty("version").GetString();

        Assert.Equal(InformationalVersion, version);
    }

    [Fact]
    public void StatusReportsTheProjectedVersion()
    {
        var status = new HistoryJanusCommands().status();

        Assert.Contains(InformationalVersion, status);
        Assert.StartsWith("HistoryJanus ", status);
    }
}
