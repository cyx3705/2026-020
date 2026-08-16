using System.IO;
using System.Text;
using HistoryVulcan.Core.Storage;
using HistoryJanus.GitHub;

namespace HistoryJanus.Git;

public sealed record WorktreeInfo(
    string BranchName,
    string WorktreePath,
    string LastCommitTime = "",
    string LastCommitMessage = "",
    bool? IsClean = null,
    string WorktreeStatusMessage = "",
    string HeadBranch = "")
{
    /// <summary>项目目录名（与已登记项目名相同）。</summary>
    public string FolderName
    {
        get
        {
            var trimmed = WorktreePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.GetFileName(trimmed);
        }
    }

    /// <summary>HEAD 不在 main 时总览可警告，不阻断列举与提交。</summary>
    public bool HasNameMismatch
        => HeadBranch.Length > 0
           && !HeadBranch.Equals(ProjectService.MainlineBranch, StringComparison.OrdinalIgnoreCase);
}

/// <summary>某项目根下以 z/Z 开头的一级 Meta 文件夹。</summary>
public sealed record MetaFolderInfo(
    string ProjectName,
    string MetaName,
    string FullPath,
    string LastWriteTime = "");

public enum CommitOutcome
{
    Success,
    Skipped,
    Rejected,
    Failed,
}

public sealed record CommitReport(
    CommitOutcome Outcome,
    string Message,
    bool HasSizeWarning = false,
    List<LargeFileEntry>? RejectedFiles = null,
    string BeforeSha = "",
    string AfterSha = "",
    IReadOnlyList<SubmoduleOperationEntry>? Submodules = null,
    bool PartialCompletion = false,
    RepositoryTarget Target = RepositoryTarget.Parent,
    bool ParentExecuted = false,
    bool ParentPointerPending = false);

/// <summary>
/// proj.* 指令域：HistoryClio 库根下一项目一仓。
/// 确认交互统一走总线确认通道，运行参数通过配置服务现读现生效。
/// </summary>
public sealed partial class ProjectService
{
    public const string KeyLibraryRoot = "proj.libraryroot";
    public const string KeyWorktreeRoot = "proj.worktreeroot";
    public const string KeyAiWorktreeRoot = "proj.aiworktreeroot";
    public const string KeyBaseBranch = "proj.basebranch";
    public const string KeyWarnMb = "proj.warnmb";
    public const string KeyRejectMb = "proj.rejectmb";
    public const string KeyProtected = "proj.protected";
    public const string MainlineBranch = ProjectRepoLayout.MainlineBranch;

    private readonly ISettingsService _settings;
    private readonly string _dataDir;
    private readonly GitlinkService _gitlinks = new();
    private readonly BranchTreeService _tree;
    private readonly Func<string, bool> _confirm;

    /// <summary>
    /// 首次推送时按需建远端仓库的通道。为空时推送保持旧行为：没有 origin 就直接失败。
    /// </summary>
    public IGitHubRepositoryProvisioner? RepositoryProvisioner { get; set; }

    public Func<IReadOnlyDictionary<string, string>>? NotesProvider
    {
        get => _tree.NotesProvider;
        set => _tree.NotesProvider = value;
    }

    public ProjectService(ISettingsService settings, Func<string, bool> confirm, string dataDir)
    {
        _settings = settings;
        _confirm = confirm;
        _dataDir = dataDir;
        _tree = new BranchTreeService(() => LibraryRoot, () => BaseBranch, ListWorktreesAsync, dataDir);
    }

    public string LibraryRoot
    {
        get
        {
            var current = _settings.Get(KeyLibraryRoot);
            if (!string.IsNullOrWhiteSpace(current))
                return current;
            var legacy = _settings.Get(KeyWorktreeRoot);
            if (!string.IsNullOrWhiteSpace(legacy)
                && !legacy.TrimEnd('\\', '/').Equals(
                    ProjectRepoLayout.LegacyVestaLibrary, StringComparison.OrdinalIgnoreCase))
                return legacy;
            return ProjectRepoLayout.DefaultLibraryRoot;
        }
    }

    public string WorktreeRoot => LibraryRoot;

    public string AiWorktreeRoot =>
        _settings.Get(KeyAiWorktreeRoot) ?? @"F:\ai工作区";

    public string BaseBranch => _settings.Get(KeyBaseBranch) ?? ProjectRepoLayout.DefaultTemplate;

    public long WarnBytes => _settings.GetInt(KeyWarnMb, 50) * 1024L * 1024;

    public long RejectBytes => _settings.GetInt(KeyRejectMb, 100) * 1024L * 1024;

    public IReadOnlySet<string> ProtectedBranches
    {
        get
        {
            var raw = _settings.Get(KeyProtected) ?? ProjectRepoLayout.DefaultTemplate;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                set.Add(name);
            set.Add(BaseBranch);
            return set;
        }
    }

    public void EnsureDefaultSettings()
    {
        SetIfMissing(KeyLibraryRoot, ProjectRepoLayout.DefaultLibraryRoot);
        SetIfMissing(KeyAiWorktreeRoot, @"F:\ai工作区");
        SetIfMissing(KeyBaseBranch, ProjectRepoLayout.DefaultTemplate);
        SetIfMissing(KeyWarnMb, "50");
        SetIfMissing(KeyRejectMb, "100");
        SetIfMissing(KeyProtected, ProjectRepoLayout.DefaultTemplate);
    }

    private void SetIfMissing(string key, string value)
    {
        if (_settings.Get(key) == null)
            _settings.Set(key, value);
    }

    public string DescribeConfig()
    {
        var sb = new StringBuilder();
        sb.AppendLine("proj.* 当前配置(app.set 键=值 可修改,即时生效):");
        sb.AppendLine($"  {KeyLibraryRoot}  = {LibraryRoot}");
        sb.AppendLine($"  {KeyAiWorktreeRoot} = {AiWorktreeRoot}");
        sb.AppendLine($"  {KeyBaseBranch}   = {BaseBranch}（模板项目名）");
        sb.AppendLine($"  {KeyWarnMb}       = {WarnBytes / 1024 / 1024} MB(警告阈值)");
        sb.AppendLine($"  {KeyRejectMb}     = {RejectBytes / 1024 / 1024} MB(LFS 阈值)");
        sb.Append($"  {KeyProtected}    = {string.Join(", ", ProtectedBranches)}");
        return sb.ToString();
    }

    public string ResolveGitHubRepository(string? projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName))
            return LibraryRoot;
        var path = Path.Combine(LibraryRoot, projectName.Trim());
        return Directory.Exists(path) ? path : LibraryRoot;
    }

    public async Task<(GitResult Git, List<WorktreeInfo> Worktrees)> ListWorktreesAsync()
    {
        if (!TryValidateWorktreeRoot(out var error))
            return (new GitResult(1, error), []);

        var list = new List<WorktreeInfo>();
        foreach (var dir in Directory.EnumerateDirectories(LibraryRoot))
        {
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                continue;
            var name = Path.GetFileName(dir);
            if (!ProjectRepoLayout.IsRegisteredProjectName(name)
                || !ProjectRepoLayout.IsIndependentGitRepo(dir))
                continue;
            list.Add(new WorktreeInfo(name, dir));
        }

        list.Sort((a, b) => string.Compare(a.BranchName, b.BranchName, StringComparison.OrdinalIgnoreCase));
        var enriched = await Task.WhenAll(list.Select(async worktree =>
        {
            var head = await GitRunner.RunAsync(worktree.WorktreePath, ["rev-parse", "--abbrev-ref", "HEAD"]);
            var log = await GitRunner.RunAsync(worktree.WorktreePath, ["log", "-1", "--format=%cI%x1f%s"]);
            var time = "";
            var subject = "";
            if (log.Success)
            {
                var parts = log.Output.Trim().Split('\u001f', 2);
                time = parts[0].Trim();
                subject = parts.Length > 1 ? parts[1].Trim() : "";
            }

            return worktree with
            {
                HeadBranch = head.Success ? head.Output.Trim() : "",
                LastCommitTime = time,
                LastCommitMessage = subject,
            };
        }));
        return (new GitResult(0, ""), [.. enriched]);
    }

    public async Task<List<WorktreeInfo>> ReadWorktreeStatusesAsync(
        IReadOnlyList<WorktreeInfo> worktrees,
        CancellationToken cancellation = default)
    {
        var tasks = worktrees.Select(async worktree =>
        {
            if (!Directory.Exists(worktree.WorktreePath))
            {
                return worktree with
                {
                    IsClean = null,
                    WorktreeStatusMessage = "项目目录不存在",
                };
            }

            var status = await GitRunner.RunAsync(
                worktree.WorktreePath,
                ["status", "--porcelain=v1", "--untracked-files=all"],
                cancellation).ConfigureAwait(false);
            if (!status.Success)
            {
                return worktree with
                {
                    IsClean = null,
                    WorktreeStatusMessage = string.IsNullOrWhiteSpace(status.Output)
                        ? "工作树状态检查失败"
                        : status.Output,
                };
            }

            var isClean = string.IsNullOrWhiteSpace(status.Output);
            return worktree with
            {
                IsClean = isClean,
                WorktreeStatusMessage = isClean ? "工作树干净" : "工作树有未提交或未跟踪的文件",
            };
        });

        return [.. await Task.WhenAll(tasks).ConfigureAwait(false)];
    }

    public async Task<(bool Success, string Message, WorktreeInfo? Worktree)> ResolveWorktreeAsync(string name)
    {
        name = name.Trim();
        if (name.Length == 0)
            return (false, "项目名称不能为空", null);

        var (git, worktrees) = await ListWorktreesAsync();
        if (!git.Success)
            return (false, $"读取已登记项目失败:\n{git.Output}", null);

        var worktree = worktrees.FirstOrDefault(item =>
            item.BranchName.Equals(name, StringComparison.OrdinalIgnoreCase)
            || item.FolderName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (worktree == null)
            return (false, $"未找到已登记项目: {name}", null);
        if (!Directory.Exists(worktree.WorktreePath))
            return (false, $"项目目录不存在: {worktree.WorktreePath}", null);
        if (!TryValidateManagedDirectChild(worktree.WorktreePath, rejectReparsePoint: true, out var error))
            return (false, $"项目目录越出受管边界: {error}", null);

        return (true, worktree.WorktreePath, worktree);
    }

    public async Task<(bool Success, string Message)> CreateAsync(
        string name, string? baseBranch, IProgress<string>? progress)
    {
        baseBranch = string.IsNullOrWhiteSpace(baseBranch) ? BaseBranch : baseBranch.Trim();
        name = name.Trim();

        if (name.Length == 0)
            return (false, "项目名称不能为空");
        if (!ProjectRepoLayout.IsRegisteredProjectName(name))
            return (false, "项目名称必须是 YYYY-NNN-* 或 0000-*");
        if (!TryValidateWorktreeRoot(out var rootError))
            return (false, $"库根不安全: {rootError}");

        var templatePath = Path.Combine(LibraryRoot, baseBranch);
        if (!ProjectRepoLayout.IsIndependentGitRepo(templatePath))
            return (false, $"模板项目仓不存在或不是独立仓库: {templatePath}(检查 {KeyBaseBranch})");

        var targetPath = Path.Combine(LibraryRoot, name);
        if (!TryValidateManagedDirectChild(targetPath, rejectReparsePoint: true, out var pathError))
            return (false, $"目标项目路径不安全: {pathError}");
        if (Directory.Exists(targetPath))
            return (false, $"目标项目目录已存在: {targetPath}\n请更换项目名称或手动清理该目录");

        progress?.Report($"从模板 {baseBranch} 复制工作树...");
        try
        {
            ProjectRepoLayout.CopyWorkingTree(templatePath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, $"复制模板工作树失败: {ex.Message}");
        }

        progress?.Report($"git init -b {MainlineBranch} ...");
        var init = await GitRunner.RunAsync(targetPath, ["init", "-b", MainlineBranch]);
        if (!init.Success)
            return (false, $"初始化仓库失败:\n{init.Output}");

        await CopyIdentityAsync(templatePath, targetPath);
        ProjectRepoLayout.StampNewProjectManifest(targetPath, baseBranch);

        progress?.Report("写入首提交...");
        var add = await GitRunner.RunAsync(targetPath, ["add", "-A"]);
        if (!add.Success)
            return (false, $"暂存首提交失败:\n{add.Output}");
        var commit = await GitRunner.RunAsync(
            targetPath, ["commit", "--allow-empty", "-m", $"Initialize from {baseBranch}"]);
        if (!commit.Success)
            return (false, $"首提交失败:\n{commit.Output}");

        return (true, $"项目已就绪: {targetPath}(独立仓,基于模板 {baseBranch})");
    }

    private static async Task CopyIdentityAsync(string templatePath, string targetPath)
    {
        var name = await GitRunner.RunAsync(templatePath, ["config", "--get", "user.name"]);
        var email = await GitRunner.RunAsync(templatePath, ["config", "--get", "user.email"]);
        var user = name.Success && name.Output.Trim().Length > 0 ? name.Output.Trim() : "HistoryJanus";
        var mail = email.Success && email.Output.Trim().Length > 0
            ? email.Output.Trim()
            : "historyjanus@localhost";
        await GitRunner.RunAsync(targetPath, ["config", "user.name", user]);
        await GitRunner.RunAsync(targetPath, ["config", "user.email", mail]);
    }

    public bool IsProtected(string branchName) => ProtectedBranches.Contains(branchName.Trim());

    public async Task<(bool Success, string Message)> DeleteAsync(string name, IProgress<string>? progress)
    {
        name = name.Trim();
        if (IsProtected(name))
            return (false, $"\"{name}\" 是受保护项目({string.Join("/", ProtectedBranches)}),拒绝删除");
        if (!ProjectRepoLayout.IsRegisteredProjectName(name))
            return (false, "项目名称必须是 YYYY-NNN-* 或 0000-*");

        var targetPath = Path.Combine(LibraryRoot, name);
        if (!Directory.Exists(targetPath))
            return (false, $"项目目录不存在: {targetPath}");
        if (!TryValidateManagedDirectChild(targetPath, rejectReparsePoint: true, out var pathError))
            return (false, $"项目路径不在受管边界内，拒绝删除: {pathError}\n{targetPath}");

        progress?.Report($"删除项目目录 {targetPath} ...");
        try
        {
            await Task.Run(() => ProjectRepoLayout.DeleteTree(targetPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, $"删除项目目录失败: {ex.Message}");
        }

        return (true, $"项目目录已删除: {targetPath}");
    }

    public async Task<(bool Success, string Message)> ScanAsync(string name)
    {
        name = name.Trim();
        var worktreePath = Path.Combine(LibraryRoot, name);
        if (!Directory.Exists(worktreePath))
            return (false, $"项目目录不存在: {worktreePath}");

        var warnBytes = WarnBytes;
        var rejectBytes = RejectBytes;
        var (status, largeFiles) = await Task.Run(() =>
            WorktreeFileScanner.ScanDirectory(worktreePath, warnBytes, rejectBytes));

        if (largeFiles.Count == 0)
            return (true, $"[{name}] 文件大小检查通过,无 ≥{warnBytes / 1024 / 1024}MB 文件");

        var sb = new StringBuilder();
        sb.Append($"[{name}] 状态: {status switch { FileSizeCheckStatus.Rejected => $"含 ≥{rejectBytes / 1024 / 1024}MB 文件(提交时需 LFS)", FileSizeCheckStatus.Warning => "仅警告级大文件", _ => "正常" }},共 {largeFiles.Count} 个大文件:");
        foreach (var file in largeFiles)
        {
            var tag = file.SizeBytes >= rejectBytes ? "LFS" : "警告";
            sb.Append($"\n  [{tag}] {file.RelativePath}({file.FormattedSize})");
        }

        return (true, sb.ToString());
    }

    public Task<(bool Success, string Message, BranchNode? Root)> BuildTreeAsync(
        IProgress<string>? progress, bool refresh = false, bool cachedOnly = false)
        => _tree.BuildTreeAsync(progress, refresh, cachedOnly);

    public sealed class BranchInfo
    {
        public required string Name { get; init; }
        public string TipSha { get; set; } = "";
        public string LastCommitTime { get; set; } = "";
        public string LastCommitMessage { get; set; } = "";
        public string Path { get; set; } = "";
    }

    public sealed class BranchNode : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isExpanded;

        public required string BranchName { get; init; }
        public string Description { get; set; } = "";
        public string LastPushTime { get; init; } = "";
        public List<BranchNode> Children { get; } = new();

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value)
                    return;
                _isExpanded = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsExpanded)));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    public Task<List<BranchInfo>?> GetAllBranchesInfoAsync(IProgress<string>? progress)
        => _tree.GetAllBranchesInfoAsync(progress);

    public Task<BranchNode> BuildInheritanceTreeAsync(
        List<BranchInfo> allBranches, BranchInfo rootInfo, IProgress<string>? progress)
        => _tree.BuildInheritanceTreeAsync(allBranches, rootInfo, progress);

    public (bool Success, string Message) OpenFolder(string? name)
    {
        var path = string.IsNullOrWhiteSpace(name)
            ? LibraryRoot
            : Path.Combine(LibraryRoot, name.Trim());
        if (!Directory.Exists(path))
            return (false, $"目录不存在: {path}");

        System.Diagnostics.Process.Start("explorer.exe", path);
        return (true, $"已在资源管理器中打开: {path}");
    }

    private bool TryValidateManagedDirectChild(
        string path,
        bool rejectReparsePoint,
        out string error)
    {
        try
        {
            if (!TryValidateWorktreeRoot(out error))
                return false;

            var root = NormalizePath(LibraryRoot);
            var candidate = NormalizePath(path);
            var parent = Directory.GetParent(candidate)?.FullName;
            if (parent == null || !PathsEqual(parent, root))
            {
                error = $"目标必须是库根的直接子目录({root})";
                return false;
            }

            if (rejectReparsePoint && Directory.Exists(candidate)
                && (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
            {
                error = "目标是符号链接或目录联接";
                return false;
            }

            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                   or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    private bool TryValidateWorktreeRoot(out string error)
    {
        try
        {
            var root = NormalizePath(LibraryRoot);
            if (!Directory.Exists(root))
            {
                error = $"目录不存在: {root}";
                return false;
            }

            var volumeRoot = Path.GetPathRoot(root);
            if (volumeRoot != null && PathsEqual(root, volumeRoot))
            {
                error = "不能把磁盘根目录配置为库根";
                return false;
            }

            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                error = "库根不能是符号链接或目录联接";
                return false;
            }

            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                   or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return NormalizePath(left).Equals(NormalizePath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var pathRoot = Path.GetPathRoot(fullPath);
        if (pathRoot != null && fullPath.Equals(pathRoot, StringComparison.OrdinalIgnoreCase))
            return pathRoot;

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
