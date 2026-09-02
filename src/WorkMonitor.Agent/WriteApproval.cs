namespace WorkMonitor.Agent;

public sealed record WriteApprovalRequest(
    string ToolName,
    string ArgumentsJson,
    string UserName,
    string? SessionId);

public interface IWriteApprovalHandler
{
    ValueTask<bool> ApproveAsync(
        WriteApprovalRequest request,
        CancellationToken cancellationToken);
}

public sealed class AllowAllWriteApprovalHandler : IWriteApprovalHandler
{
    public ValueTask<bool> ApproveAsync(
        WriteApprovalRequest request,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(true);
}

public sealed class DenyWriteApprovalHandler : IWriteApprovalHandler
{
    public ValueTask<bool> ApproveAsync(
        WriteApprovalRequest request,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(false);
}
