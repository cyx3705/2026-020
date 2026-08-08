namespace HistoryJanus.Git;

/// <summary>台账中的一种文件格式。Suggestion 由规则建议引擎填充。</summary>
public sealed record FormatRow(
    string Format,
    int FileCount,
    int ProjectCount,
    int TrackedCount,
    int IgnoredCount,
    int UndecidedCount,
    long MaxBytes,
    long TotalBytes,
    string RuleState,
    bool? Track,
    bool? Lfs,
    bool? Lf,
    string Suggestion = "",
    bool BinarySniff = false);

/// <summary>无扩展名/杂项文件的目录候选(§3.3:只有目录规则能闭合这部分)。</summary>
public sealed record DirectoryCandidateRow(
    string Project,
    string Directory,
    int FileCount,
    bool Ignored,
    string Suggestion = "");

/// <summary>一次扫描的完整台账。</summary>
public sealed record InventoryReport(
    int ProjectCount,
    int FileCount,
    int TrackedCount,
    int IgnoredCount,
    int UndecidedCount,
    double CoverageRate,
    string Depth,
    int CachedProjects,
    long ElapsedMs,
    IReadOnlyList<FormatRow> Formats,
    IReadOnlyList<DirectoryCandidateRow> Directories);

/// <summary>一次扫描生成的未决清单、自动建议和需人工判断的未知格式。</summary>
public sealed record GitRuleReviewReport(
    InventoryReport Gaps,
    IReadOnlyList<RuleSuggestion> Suggestions,
    IReadOnlyList<FormatRow> UnknownFormats,
    int SuggestedFileCount,
    double SuggestedCoverageRate);
