using System.IO;
using System.Text;
using HistoryVulcan.Services;

namespace HistoryJanus.Git;

/// <summary>
/// 继承树：按各仓 project.manifest.json 的 template.source 建树，缓存键为库根。
/// </summary>
public sealed class BranchTreeService
{
    private readonly Func<string> _libraryRoot;
    private readonly Func<string> _baseBranch;
    private readonly Func<Task<(GitResult Git, List<WorktreeInfo> Worktrees)>> _listProjects;
    private readonly string _dataDir;

    public BranchTreeService(
        Func<string> libraryRoot,
        Func<string> baseBranch,
        Func<Task<(GitResult Git, List<WorktreeInfo> Worktrees)>> listProjects,
        string dataDir)
    {
        _libraryRoot = libraryRoot;
        _baseBranch = baseBranch;
        _listProjects = listProjects;
        _dataDir = dataDir;
    }

    private string LibraryRoot => _libraryRoot();

    private string BaseBranch => _baseBranch();

    public Func<IReadOnlyDictionary<string, string>>? NotesProvider { get; set; }

    public async Task<(bool Success, string Message, ProjectService.BranchNode? Root)> BuildTreeAsync(
        IProgress<string>? progress, bool refresh = false, bool cachedOnly = false)
    {
        if (!refresh)
        {
            var cached = LoadTreeCache();
            if (cached != null)
            {
                var (builtAt, count, cachedRoot) = cached.Value;
                ApplyNotes(cachedRoot);
                var sbc = new StringBuilder();
                sbc.Append($"分支继承树(共 {count} 个项目,根 = {BaseBranch};缓存于 {builtAt:yyyy-MM-dd HH:mm},janus.proj.tree refresh=true 重新扫描):");
                RenderTree(cachedRoot, sbc, "", isRoot: true);
                return (true, sbc.ToString(), cachedRoot);
            }

            if (cachedOnly)
                return (true, "尚无继承树缓存,点击「重新扫描」或执行 janus.proj.tree refresh=true 构建", null);
        }

        if (!Directory.Exists(LibraryRoot))
            return (false, $"库根不存在: {LibraryRoot}", null);

        var branches = await GetAllBranchesInfoAsync(progress);
        if (branches == null)
            return (false, "无法获取项目列表", null);
        if (branches.Count == 0)
            return (false, "未发现任何已登记项目", null);

        var root = branches.FirstOrDefault(
            b => b.Name.Equals(BaseBranch, StringComparison.OrdinalIgnoreCase));
        if (root == null)
            return (false, $"未找到根模板项目: {BaseBranch}", null);

        var rootNode = await BuildInheritanceTreeAsync(branches, root, progress);
        SaveTreeCache(rootNode, branches.Count, progress);

        ApplyNotes(rootNode);
        var sb = new StringBuilder();
        sb.Append($"分支继承树(共 {branches.Count} 个项目,根 = {BaseBranch};已缓存):");
        RenderTree(rootNode, sb, "", isRoot: true);
        return (true, sb.ToString(), rootNode);
    }

    private void ApplyNotes(ProjectService.BranchNode root)
    {
        var notes = NotesProvider?.Invoke();
        if (notes == null || notes.Count == 0)
            return;
        Overlay(root);

        void Overlay(ProjectService.BranchNode node)
        {
            if (notes.TryGetValue(node.BranchName, out var note) && note.Length > 0)
                node.Description = note;
            foreach (var child in node.Children)
                Overlay(child);
        }
    }

    public async Task<List<ProjectService.BranchInfo>?> GetAllBranchesInfoAsync(IProgress<string>? progress)
    {
        var (git, worktrees) = await _listProjects();
        if (!git.Success)
            return null;

        var list = new List<ProjectService.BranchInfo>();
        for (var i = 0; i < worktrees.Count; i++)
        {
            var item = worktrees[i];
            progress?.Report($"读取项目 {i + 1}/{worktrees.Count}: {item.BranchName}");
            var sha = await GitRunner.RunAsync(item.WorktreePath, ["rev-parse", "HEAD"]);
            list.Add(new ProjectService.BranchInfo
            {
                Name = item.BranchName,
                Path = item.WorktreePath,
                TipSha = sha.Success ? sha.Output.Trim() : "",
                LastCommitTime = item.LastCommitTime,
                LastCommitMessage = item.LastCommitMessage.Length > 0
                    ? item.LastCommitMessage
                    : "(无法获取提交信息)",
            });
        }

        return list;
    }

    public Task<ProjectService.BranchNode> BuildInheritanceTreeAsync(
        List<ProjectService.BranchInfo> allBranches,
        ProjectService.BranchInfo rootInfo,
        IProgress<string>? progress)
    {
        var names = allBranches.ToDictionary(b => b.Name, StringComparer.OrdinalIgnoreCase);
        var nodeMap = allBranches.ToDictionary(
            b => b.Name,
            b => new ProjectService.BranchNode
            {
                BranchName = b.Name,
                Description = string.IsNullOrWhiteSpace(b.LastCommitMessage) ? "(无提交信息)" : b.LastCommitMessage,
                LastPushTime = b.LastCommitTime,
            },
            StringComparer.OrdinalIgnoreCase);

        var parentOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in allBranches)
        {
            if (child.Name.Equals(rootInfo.Name, StringComparison.OrdinalIgnoreCase))
                continue;

            progress?.Report($"解析继承 {child.Name}");
            var source = string.IsNullOrWhiteSpace(child.Path)
                ? null
                : ProjectRepoLayout.ReadTemplateSource(child.Path);
            if (!string.IsNullOrWhiteSpace(source)
                && names.ContainsKey(source)
                && !source.Equals(child.Name, StringComparison.OrdinalIgnoreCase))
                parentOf[child.Name] = source;
            else
                parentOf[child.Name] = rootInfo.Name;
        }

        while (true)
        {
            var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootInfo.Name };
            bool grew;
            do
            {
                grew = false;
                foreach (var (child, parent) in parentOf)
                {
                    if (!reachable.Contains(child) && reachable.Contains(parent))
                    {
                        reachable.Add(child);
                        grew = true;
                    }
                }
            }
            while (grew);

            var unreachable = parentOf.Keys.Where(n => !reachable.Contains(n)).ToList();
            if (unreachable.Count == 0)
                break;
            parentOf[unreachable.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).First()] = rootInfo.Name;
        }

        foreach (var (child, parent) in parentOf)
            nodeMap[parent].Children.Add(nodeMap[child]);

        SortChildren(nodeMap[rootInfo.Name]);
        return Task.FromResult(nodeMap[rootInfo.Name]);
    }

    private static void SortChildren(ProjectService.BranchNode node)
    {
        node.Children.Sort((a, b) => string.Compare(a.BranchName, b.BranchName, StringComparison.OrdinalIgnoreCase));
        foreach (var child in node.Children)
            SortChildren(child);
    }

    private static void RenderTree(
        ProjectService.BranchNode node, StringBuilder sb, string prefix, bool isRoot)
    {
        if (isRoot)
            sb.Append($"\n{node.BranchName}  [{node.LastPushTime}]  {node.Description}");

        var children = node.Children;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            var isLast = i == children.Count - 1;
            sb.Append($"\n{prefix}{(isLast ? "└─ " : "├─ ")}{child.BranchName}  [{child.LastPushTime}]  {child.Description}");
            RenderTree(child, sb, prefix + (isLast ? "   " : "│  "), isRoot: false);
        }
    }

    private string TreeCachePath => Path.Combine(AppPaths.GetDataDir(_dataDir), "branch-tree.json");

    private sealed record TreeCacheNode(string Name, string Time, string Desc, List<TreeCacheNode> Children);

    private sealed record TreeCacheFile(DateTime BuiltAt, int BranchCount, string LibraryRoot, TreeCacheNode Root);

    private void SaveTreeCache(ProjectService.BranchNode root, int branchCount, IProgress<string>? progress)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TreeCachePath)!);
            var file = new TreeCacheFile(DateTime.Now, branchCount, LibraryRoot, ToCacheNode(root));
            File.WriteAllText(TreeCachePath, System.Text.Json.JsonSerializer.Serialize(
                file, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            progress?.Report($"继承树已缓存: {TreeCachePath}");
        }
        catch (Exception ex)
        {
            progress?.Report($"继承树缓存写入失败(不影响本次结果): {ex.Message}");
        }
    }

    private (DateTime BuiltAt, int Count, ProjectService.BranchNode Root)? LoadTreeCache()
    {
        try
        {
            if (!File.Exists(TreeCachePath))
                return null;
            var file = System.Text.Json.JsonSerializer.Deserialize<TreeCacheFile>(
                File.ReadAllText(TreeCachePath));
            if (file?.Root == null)
                return null;
            if (!string.Equals(file.LibraryRoot, LibraryRoot, StringComparison.OrdinalIgnoreCase))
                return null;
            return (file.BuiltAt, file.BranchCount, FromCacheNode(file.Root));
        }
        catch
        {
            return null;
        }
    }

    private static TreeCacheNode ToCacheNode(ProjectService.BranchNode node) => new(
        node.BranchName, node.LastPushTime, node.Description,
        node.Children.Select(ToCacheNode).ToList());

    private static ProjectService.BranchNode FromCacheNode(TreeCacheNode dto)
    {
        var node = new ProjectService.BranchNode
        {
            BranchName = dto.Name,
            LastPushTime = dto.Time,
            Description = dto.Desc,
        };
        foreach (var child in dto.Children)
            node.Children.Add(FromCacheNode(child));
        return node;
    }
}
