using HistoryJanus.Git;
using Xunit;

namespace HistoryJanus.Contracts;

/// <summary>
/// 新建项目复制的是模板工作树，不是克隆。根下 z/Z 文件夹是模板仓自己的元数据或备用模板
/// （0000-000-Template 的 z-Unused），不带进新仓；要用时再从模板仓复制。
/// 嵌在普通目录里的 z 段、以及根上以 z 开头的文件，都不是这一级文件夹。
/// </summary>
public sealed class ProjectInheritContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HistoryJanus-Inherit", Guid.NewGuid().ToString("N"));

    [Fact]
    public void CopyWorkingTreeSkipsTopLevelZFoldersOnly()
    {
        var source = Path.Combine(_root, "template");
        var destination = Path.Combine(_root, "child");
        Directory.CreateDirectory(Path.Combine(source, "b-Office"));
        File.WriteAllText(Path.Combine(source, "b-Office", "note.md"), "keep");
        File.WriteAllText(Path.Combine(source, "README.md"), "root");
        File.WriteAllText(Path.Combine(source, "z-note.txt"), "root-file");
        Directory.CreateDirectory(Path.Combine(source, "z-Unused", "b-Altium-Designer"));
        File.WriteAllText(Path.Combine(source, "z-Unused", "b-Altium-Designer", "board.txt"), "template");
        Directory.CreateDirectory(Path.Combine(source, "Z-Meta"));
        File.WriteAllText(Path.Combine(source, "Z-Meta", "local.txt"), "meta");
        Directory.CreateDirectory(Path.Combine(source, "b-Code", "z-nested"));
        File.WriteAllText(Path.Combine(source, "b-Code", "z-nested", "stay.txt"), "nested");
        Directory.CreateDirectory(Path.Combine(source, ".git"));
        File.WriteAllText(Path.Combine(source, ".git", "HEAD"), "ref: refs/heads/main");

        ProjectRepoLayout.CopyWorkingTree(source, destination);

        Assert.Equal("keep", File.ReadAllText(Path.Combine(destination, "b-Office", "note.md")));
        Assert.Equal("root", File.ReadAllText(Path.Combine(destination, "README.md")));
        Assert.Equal("root-file", File.ReadAllText(Path.Combine(destination, "z-note.txt")));
        Assert.Equal("nested", File.ReadAllText(Path.Combine(destination, "b-Code", "z-nested", "stay.txt")));
        Assert.False(Directory.Exists(Path.Combine(destination, "z-Unused")));
        Assert.False(Directory.Exists(Path.Combine(destination, "Z-Meta")));
        Assert.False(Directory.Exists(Path.Combine(destination, ".git")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
