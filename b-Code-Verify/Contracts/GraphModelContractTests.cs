using System.Reflection;
using System.Text.Json;
using HistoryJanus.Git;
using Xunit;

namespace HistoryJanus.Contracts;

/// <summary>
/// 图谱 DTO 跨进程合同：JSON 可往返，且不得携带 WPF / 展开状态 / 布局坐标。
/// </summary>
public sealed class GraphModelContractTests
{
    private static readonly JsonSerializerOptions Camel = CreateCamelOptions();
    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    private static readonly string[] ForbiddenPropertyNames =
    [
        "IsExpanded", "X", "Y", "Left", "Top", "Width", "Height",
        "CanvasLeft", "CanvasTop", "LayoutX", "LayoutY",
    ];

    [Fact]
    public void GraphCommitsReportSurvivesJsonRoundTrip()
    {
        var source = new GraphCommitsReport
        {
            ProjectName = "2026-020-HistoryJanus",
            Name = "2026-020-HistoryJanus",
            Page = new GraphPage { Skip = 0, Limit = 200, HasMore = true, UntilSha = "aa" },
            Nodes =
            [
                new GraphCommitNode
                {
                    Sha = "0123456789abcdef0123456789abcdef01234567",
                    ShortSha = "0123456789",
                    Parents = ["fedcba9876543210fedcba9876543210fedcba98"],
                    Author = "author",
                    CommittedAt = new DateTimeOffset(2026, 8, 13, 1, 0, 0, TimeSpan.Zero),
                    Subject = "merge topic",
                    FileSummary = "",
                    BranchRefs = ["2026-020-HistoryJanus"],
                    ReleaseId = "",
                    ReleaseStatus = "",
                },
            ],
            Edges =
            [
                new GraphEdge
                {
                    FromSha = "fedcba9876543210fedcba9876543210fedcba98",
                    ToSha = "0123456789abcdef0123456789abcdef01234567",
                    IsMergeParent = true,
                },
            ],
            Lanes =
            [
                new GraphRef
                {
                    Name = "2026-020-HistoryJanus",
                    FullName = "refs/heads/2026-020-HistoryJanus",
                    TargetSha = "0123456789abcdef0123456789abcdef01234567",
                    Kind = BranchKind.Mainline,
                    IsOpen = true,
                },
                new GraphRef
                {
                    Name = "ai/2026-020-HistoryJanus/0123456-1-demo",
                    FullName = "refs/heads/ai/2026-020-HistoryJanus/0123456-1-demo",
                    TargetSha = "fedcba9876543210fedcba9876543210fedcba98",
                    Kind = BranchKind.AiWork,
                    IsOpen = false,
                },
            ],
        };

        var json = JsonSerializer.SerializeToElement(source, Camel);
        var restored = Assert.IsType<GraphCommitsReport>(
            JsonSerializer.Deserialize<GraphCommitsReport>(json.GetRawText(), Camel));

        Assert.Equal(source.ProjectName, restored.ProjectName);
        Assert.True(restored.Page.HasMore);
        Assert.Equal("aa", restored.Page.UntilSha);
        var node = Assert.Single(restored.Nodes);
        Assert.Equal(source.Nodes[0].Sha, node.Sha);
        Assert.Equal(source.Nodes[0].ShortSha, node.ShortSha);
        Assert.Equal(source.Nodes[0].Author, node.Author);
        Assert.Equal(source.Nodes[0].Subject, node.Subject);
        Assert.Equal("", node.FileSummary);
        Assert.Equal("", node.ReleaseId);
        Assert.Equal("", node.ReleaseStatus);
        Assert.True(Assert.Single(restored.Edges).IsMergeParent);
        Assert.Equal(2, restored.Lanes.Count);
        Assert.False(restored.Lanes[1].IsOpen);
        Assert.DoesNotContain("isExpanded", json.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"sha\":", json.GetRawText());
        Assert.Contains("\"shortSha\":", json.GetRawText());
        Assert.Contains("\"isMergeParent\":", json.GetRawText());
        Assert.Contains("\"fileSummary\":", json.GetRawText());
        Assert.Contains("\"untilSha\":", json.GetRawText());
    }

    [Fact]
    public void SummaryBranchesAndNodeDetailRoundTripWithoutLayout()
    {
        var mainline = new GraphRef
        {
            Name = "2026-020-HistoryJanus",
            FullName = "refs/heads/2026-020-HistoryJanus",
            IsRemote = false,
            TargetSha = "0123456789abcdef0123456789abcdef01234567",
            Kind = BranchKind.Mainline,
            IsOpen = true,
        };
        var summary = new GraphSummary
        {
            ProjectName = "2026-020-HistoryJanus",
            HeadSha = mainline.TargetSha,
            HeadShortSha = "0123456789",
            NodeCount = 1,
            EdgeCount = 0,
            BranchCount = 1,
            IsDirty = false,
            Branches = [mainline],
        };
        var branches = new GraphBranchesReport
        {
            ProjectName = summary.ProjectName,
            Mainline = mainline,
            Inheritance = [],
            AiWork =
            [
                new GraphRef
                {
                    Name = "ai/2026-020-HistoryJanus/0123456-1-demo",
                    FullName = "refs/heads/ai/2026-020-HistoryJanus/0123456-1-demo",
                    TargetSha = mainline.TargetSha,
                    Kind = BranchKind.AiWork,
                    IsOpen = false,
                },
            ],
            Parallels =
            [
                new GraphRef
                {
                    Name = "ai/2026-020-HistoryJanus/0123456-1-demo",
                    FullName = "refs/heads/ai/2026-020-HistoryJanus/0123456-1-demo",
                    TargetSha = mainline.TargetSha,
                    Kind = BranchKind.AiWork,
                    IsOpen = false,
                },
            ],
            Relations =
            [
                new GraphBranchRelation
                {
                    BranchName = mainline.Name,
                    Kind = BranchKind.Mainline,
                    ParentBranch = "0000-000-Template",
                    BaselineSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                },
            ],
        };
        var detail = new GraphNodeDetail
        {
            Node = new GraphCommitNode
            {
                Sha = mainline.TargetSha,
                ShortSha = "0123456789",
                Author = "author",
                CommittedAt = new DateTimeOffset(2026, 8, 13, 2, 0, 0, TimeSpan.Zero),
                Subject = "head",
            },
            ParentEdges =
            [
                new GraphEdge
                {
                    FromSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    ToSha = mainline.TargetSha,
                    IsMergeParent = false,
                },
            ],
            FileSummary = "1 file changed",
            DiffSummary = "",
            ReleaseId = "",
            ReleaseStatus = "",
            Evidence = "",
        };

        AssertRoundTrip(summary);
        AssertRoundTrip(branches);
        AssertRoundTrip(detail);
        Assert.False(Assert.Single(branches.Parallels).IsOpen);
        Assert.Equal("", detail.ReleaseId);
        Assert.Equal("", detail.ReleaseStatus);
    }

    [Fact]
    public void UnknownFieldsAreForwardCompatible()
    {
        using var json = JsonDocument.Parse(
            """{"sha":"abc","shortSha":"abc","parents":[],"author":"a","committedAt":"2026-08-13T00:00:00+00:00","subject":"s","fileSummary":"","branchRefs":[],"releaseId":"","releaseStatus":"","future":true}""");

        var restored = Assert.IsType<GraphCommitNode>(
            JsonSerializer.Deserialize<GraphCommitNode>(json.RootElement.GetRawText(), CaseInsensitive));
        Assert.Equal("abc", restored.Sha);
        Assert.Equal("", restored.FileSummary);
        Assert.Equal("", restored.ReleaseId);
    }

    [Fact]
    public void GraphDtosContainNoWpfOrExpansionFields()
    {
        var types = new[]
        {
            typeof(GraphRef), typeof(GraphCommitNode), typeof(GraphEdge), typeof(GraphPage),
            typeof(GraphBranchRelation), typeof(GraphSummary), typeof(GraphBranchesReport),
            typeof(GraphCommitsReport), typeof(GraphNodeDetail), typeof(BranchKind),
        };

        foreach (var type in types)
        {
            Assert.DoesNotContain("System.Windows", type.Namespace ?? "", StringComparison.Ordinal);
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.DoesNotContain(property.Name, ForbiddenPropertyNames);
                var fullName = property.PropertyType.FullName ?? "";
                Assert.DoesNotContain("System.Windows", fullName, StringComparison.Ordinal);
            }
        }
    }

    private static void AssertRoundTrip<T>(T source)
    {
        var json = JsonSerializer.SerializeToElement(source, Camel);
        var restored = Assert.IsType<T>(
            JsonSerializer.Deserialize<T>(json.GetRawText(), Camel));
        Assert.NotNull(restored);
        Assert.DoesNotContain("isExpanded", json.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("canvasLeft", json.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("layoutX", json.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    private static JsonSerializerOptions CreateCamelOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
