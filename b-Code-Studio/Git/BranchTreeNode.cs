namespace HistoryJanus.Git;

/// <summary>janus.proj.tree 跨进程传输合同；不携带 WPF 控件或展开状态。</summary>
public sealed class BranchTreeNode
{
    public required string BranchName { get; init; }

    public string Description { get; init; } = "";

    public string LastPushTime { get; init; } = "";

    public List<BranchTreeNode> Children { get; init; } = [];

    public static BranchTreeNode FromDomain(ProjectService.BranchNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return new BranchTreeNode
        {
            BranchName = node.BranchName,
            Description = node.Description,
            LastPushTime = node.LastPushTime,
            Children = node.Children.Select(FromDomain).ToList(),
        };
    }

    public bool TryValidate(out int nodeCount, out string error)
    {
        nodeCount = 0;
        error = "";
        return Validate(this, ref nodeCount, ref error);
    }

    private static bool Validate(BranchTreeNode node, ref int nodeCount, ref string error)
    {
        if (string.IsNullOrWhiteSpace(node.BranchName))
        {
            error = "继承树包含空分支名";
            return false;
        }
        if (node.Children == null)
        {
            error = $"继承树节点 {node.BranchName} 缺少 Children";
            return false;
        }

        nodeCount++;
        foreach (var child in node.Children)
        {
            if (child == null || !Validate(child, ref nodeCount, ref error))
                return false;
        }
        return true;
    }
}
