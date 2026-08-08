namespace HistoryJanus.GitHub;

public enum GitRemoteTransport
{
    None,
    Ssh,
    Https,
    Other,
}

public sealed record GitIdentityInfo(string Name, string Email, string Source);

public sealed record GitRemoteInfo(
    string FetchUrl,
    string PushUrl,
    GitRemoteTransport Transport,
    string Host,
    string Owner,
    string Repository);

public sealed record GitCredentialAccount(string Account);

public sealed record SshPublicKeyInfo(string FileName, string Algorithm, string Fingerprint);

public sealed record SshAccountStatus(
    string Host,
    string HostAlias,
    IReadOnlyList<SshPublicKeyInfo> PublicKeys,
    string ProbeState,
    string ProbeMessage);

public sealed record GitHubDiagnosticStep(
    string Step,
    string State,
    long DurationMs,
    string Detail);

public sealed record GitHubConnectionStatus(
    string State,
    string Transport,
    IReadOnlyList<GitHubDiagnosticStep> Steps);

public sealed record GitHubAccountOverview(
    string GitVersion,
    string CredentialManagerVersion,
    bool GhCliAvailable,
    GitIdentityInfo EffectiveIdentity,
    GitRemoteInfo Origin,
    IReadOnlyList<GitCredentialAccount> CredentialAccounts,
    SshAccountStatus Ssh,
    GitHubConnectionStatus Connection,
    DateTimeOffset LastCheckedAt);

public sealed record GitHubMutationResult<T>(T Before, T After, bool Applied, string Action);
