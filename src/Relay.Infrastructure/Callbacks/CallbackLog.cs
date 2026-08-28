using Microsoft.EntityFrameworkCore;
using Relay.Application.Callbacks;
using Relay.Infrastructure.Persistence;

namespace Relay.Infrastructure.Callbacks;

/// <summary>
/// Writes the callback audit log.
/// </summary>
internal sealed class CallbackLog(RelayDbContext context) : ICallbackLog
{
    public Task RecordAppliedAsync(
        CallbackSubmission submission,
        int carried,
        int applied,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken)
    {
        // Added to the change tracker, not committed. The caller commits the
        // message changes and this row together, so a callback either lands whole
        // or not at all — a saved audit row claiming an application that rolled
        // back would be worse than no row.
        context.CallbackRecords.Add(CallbackRecord.Of(
            submission.ProviderId,
            applied > 0 ? CallbackDisposition.Applied : CallbackDisposition.NoOp,
            detail: null,
            carried,
            applied,
            submission.Body,
            receivedAt));

        return Task.CompletedTask;
    }

    public async Task RecordFailedAsync(
        CallbackSubmission submission,
        CallbackVerification verification,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken)
    {
        context.CallbackRecords.Add(CallbackRecord.Of(
            submission.ProviderId,
            verification.Verdict is CallbackVerdictKind.Malformed
                ? CallbackDisposition.Malformed
                : CallbackDisposition.Rejected,
            verification.Detail,
            receiptCount: 0,
            appliedCount: 0,
            submission.Body,
            receivedAt));

        // Committed here rather than deferred. This path returns without reaching
        // the unit of work, so a row left in the change tracker would be discarded
        // — and a rejected callback is the one most worth having recorded.
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordUnknownProviderAsync(
        CallbackSubmission submission,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken)
    {
        context.CallbackRecords.Add(CallbackRecord.Of(
            submission.ProviderId,
            CallbackDisposition.UnknownProvider,
            detail: null,
            receiptCount: 0,
            appliedCount: 0,
            submission.Body,
            receivedAt));

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
