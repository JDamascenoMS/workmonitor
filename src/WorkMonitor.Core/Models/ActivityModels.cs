namespace WorkMonitor.Core.Models;

public sealed record RecentCommentActivity(
    int CommentId,
    string Text,
    DateTimeOffset? ModifiedAt);

public sealed record RecentWorkItemActivity(
    int Id,
    string Title,
    string WorkItemType,
    string State,
    int Priority,
    string AreaPath,
    string AssignedTo,
    DateTimeOffset? ChangedAt,
    IReadOnlyList<RecentCommentActivity> RecentComments);

public sealed record RecentActivityResult(
    int CandidateCount,
    bool CandidateWindowTruncated,
    IReadOnlyList<RecentWorkItemActivity> Items,
    bool ResultTruncated = false);

public sealed record RecentMentionReference(
    int WorkItemId,
    DateTimeOffset? MentionedAt);

public sealed record CommentScanResult(
    int TotalCount,
    bool IsPartial,
    IReadOnlyList<Comment> Comments);
