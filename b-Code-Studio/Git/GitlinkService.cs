namespace HistoryJanus.Git;

public enum GitlinkKind
{
    StandardSubmodule,
    LegacyGitlink,
}

public enum SubmoduleOperationOutcome
{
    Skipped,
    Success,
    Rejected,
    Failed,
}

public enum RepositoryTarget
{
    Parent,
    Submodules,
    Both,
}

public sealed record GitlinkDescriptor(
    string RelativePath,
    string FullPath,
    GitlinkKind Kind,
    string Name,
    string IndexSha,
    string HeadSha,
    string Branch,
    bool IsDirty,
    int ModifiedCount,
    int DeletedCount,
    int UntrackedCount,
    bool NestedGitlinkDirty,
    bool HasIdentity,
    bool HasOrigin);

public sealed record SubmoduleOperationEntry(
    string RelativePath,
    string Branch,
    string BeforeSha,
    string AfterSha,
    SubmoduleOperationOutcome Outcome,
    string Message,
    GitlinkKind Kind,
    bool Pushed = false);

/// <summary>本次推送为某个项目新建（或复用）的远端仓库。</summary>
public sealed record CreatedRemoteEntry(
    string Project,
    string FullName,
    string Url,
    string Visibility,
    bool Created);

public sealed record PushReport(
    bool Success,
    string Message,
    bool ParentPushed = false,
    IReadOnlyList<SubmoduleOperationEntry>? Submodules = null,
    bool PartialCompletion = false,
    RepositoryTarget Target = RepositoryTarget.Parent,
    bool ParentPointerPending = false,
    bool RemoteCreated = false,
    string RemoteUrl = "",
    string RemoteVisibility = "");

public sealed record ProjectCommitResult(string Project, CommitReport Report);

public sealed record BatchCommitReport(
    bool Success,
    string Message,
    IReadOnlyList<ProjectCommitResult> Projects,
    bool PartialCompletion = false,
    RepositoryTarget Target = RepositoryTarget.Parent,
    int ParentPointerPendingCount = 0);

public sealed record BatchPushReport(
    bool Success,
    string Message,
    bool ParentPushed,
    IReadOnlyList<SubmoduleOperationEntry> Submodules,
    bool PartialCompletion = false,
    RepositoryTarget Target = RepositoryTarget.Parent,
    int ParentPointerPendingCount = 0,
    IReadOnlyList<CreatedRemoteEntry>? CreatedRemotes = null);
