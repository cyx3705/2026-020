using System.IO;
using HistoryJanus.Git;
using Xunit;

namespace HistoryJanus.Contracts;

/// <summary>
/// 提交前的差异弹窗里，文件名必须是人看得懂的那串字（5.10.0，REQ-020）。
///
/// git 默认 <c>core.quotepath=true</c>，凡是输出路径的命令都会把非 ASCII 字节写成
/// <c>\344\270\255</c> 这种三位八进制码。本库的项目名与文件名大量是中文，
/// 于是弹窗里整排文件变成数字串。
/// </summary>
public sealed class GitPathDisplayContractTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "HistoryJanus-GitPath", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// 走 <see cref="GitRunner"/> 的每一条命令都不做八进制转义。这里同时查
    /// 已跟踪文件（<c>diff --stat</c>）与未跟踪文件（<c>status --porcelain</c>）两条路——
    /// 弹窗正文两段分别来自它们。
    /// </summary>
    [Fact]
    public async Task GitPrintsChinesePathsAsCharactersNotOctalEscapes()
    {
        const string tracked = "已跟踪的中文文件.txt";
        const string untracked = "未跟踪的中文文件.txt";
        Directory.CreateDirectory(_root);
        await Git("init", "-b", "main");
        await Git("config", "user.name", "Janus Test");
        await Git("config", "user.email", "janus@example.invalid");
        File.WriteAllText(Path.Combine(_root, tracked), "one\n");
        await Git("add", "-A");
        await Git("commit", "-m", "initial");
        File.WriteAllText(Path.Combine(_root, tracked), "two\n");
        File.WriteAllText(Path.Combine(_root, untracked), "new\n");

        var stat = await GitRunner.RunAsync(_root, ["diff", "--stat", "HEAD"]);
        Assert.True(stat.Success, stat.Output);
        Assert.Contains(tracked, stat.Output);
        Assert.DoesNotContain("\\345", stat.Output);

        var status = await GitRunner.RunAsync(_root, ["status", "--porcelain=v1", "--untracked-files=all"]);
        Assert.True(status.Success, status.Output);
        Assert.Contains(untracked, status.Output);
        Assert.DoesNotContain("\\346", status.Output);
    }

    /// <summary>
    /// <c>core.quotepath=false</c> 只管非 ASCII。路径里有引号、反斜杠或控制字符时，
    /// git 仍会把整条引起来并做 C 风格转义，原样显示就多出一对引号和 <c>\"</c> 残留。
    /// 没被引起来的路径必须原样返回——绝不猜测。
    /// </summary>
    [Theory]
    [InlineData("b-Code/普通路径.txt", "b-Code/普通路径.txt")]
    [InlineData("有空格 的文件.txt", "有空格 的文件.txt")]
    [InlineData("\"带\\\"引号\\\"的名字.txt\"", "带\"引号\"的名字.txt")]
    [InlineData("\"反斜杠\\\\结尾\"", "反斜杠\\结尾")]
    [InlineData("\"\\344\\270\\255\\346\\226\\207.txt\"", "中文.txt")]
    [InlineData("\"换行\\n结尾\"", "换行\n结尾")]
    [InlineData("\"", "\"")]
    [InlineData("", "")]
    public void QuotedPathsAreRestoredAndPlainOnesAreLeftAlone(string raw, string expected)
        => Assert.Equal(expected, GitPath.Unquote(raw));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) ProjectRepoLayout.DeleteTree(_root); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private Task Git(params string[] args) => RunAsync(_root, args);

    private static async Task RunAsync(string directory, string[] args)
    {
        var result = await GitRunner.RunAsync(directory, args);
        Assert.True(result.Success, $"git {string.Join(' ', args)} failed: {result.Output}");
    }
}
