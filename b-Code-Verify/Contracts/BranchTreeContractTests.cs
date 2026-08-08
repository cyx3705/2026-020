using System.Text.Json;
using HistoryJanus.Git;
using Xunit;

namespace HistoryJanus.Contracts;

public sealed class BranchTreeContractTests
{
    [Fact]
    public void RecursiveTreeSurvivesJsonRoundTrip()
    {
        var source = new BranchTreeNode
        {
            BranchName = "root",
            Description = "template",
            LastPushTime = "2026-07-28",
            Children =
            [
                new BranchTreeNode
                {
                    BranchName = "child",
                    Description = "level 1",
                    Children =
                    [
                        new BranchTreeNode { BranchName = "leaf-a", Description = "level 2" },
                        new BranchTreeNode { BranchName = "leaf-b", LastPushTime = "now" },
                    ],
                },
            ],
        };

        var json = JsonSerializer.SerializeToElement(source);
        var restored = Assert.IsType<BranchTreeNode>(
            JsonSerializer.Deserialize<BranchTreeNode>(
                json.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }));

        Assert.True(restored.TryValidate(out var nodeCount, out var error), error);
        Assert.Equal(4, nodeCount);
        Assert.Equal("child", Assert.Single(restored.Children).BranchName);
        Assert.Equal(["leaf-a", "leaf-b"], restored.Children[0].Children.Select(node => node.BranchName));
        Assert.DoesNotContain("isExpanded", json.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownFieldsAreForwardCompatible()
    {
        using var json = JsonDocument.Parse(
            """{"branchName":"root","description":"","lastPushTime":"","children":[],"future":true}""");

        var restored = Assert.IsType<BranchTreeNode>(
            JsonSerializer.Deserialize<BranchTreeNode>(
                json.RootElement.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }));

        Assert.Empty(restored.Children);
        Assert.Equal("root", restored.BranchName);
    }

    [Fact]
    public void InvalidNodeIsDiagnosable()
    {
        var node = new BranchTreeNode { BranchName = " " };

        Assert.False(node.TryValidate(out var nodeCount, out var error));
        Assert.Equal(0, nodeCount);
        Assert.Contains("空分支名", error);
    }
}
