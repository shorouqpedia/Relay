using Microsoft.Extensions.Logging;
using Relay.Application.Delivery;
using Relay.Domain.Common;
using Relay.Domain.Messaging;

namespace Relay.Application.Callbacks;

/// <summary>A callback as it arrived, in terms the application can handle.</summary>
/// <param name="ProviderId">The provider named in the route.</param>
/// <param name="Headers">Request headers.</param>
/// <param name="Body">The raw body, unparsed — a signature covers bytes, not objects.</param>
public sealed record CallbackSubmission(
    string ProviderId,
    IReadOnlyDictionary<string, string> Headers,
    string Body);

/// <summary>What the endpoint should do about a callback.</summary>
public enum CallbackDecision
{
    /// <summary>Unset.</summary>
    None = 0,

    /// <summary>Acknowledge it. Includes callbacks that changed nothing.</summary>
    Acknowledge = 1,

    /// <summary>Refuse it: the signature or timestamp did not hold up.</summary>
    Refuse = 2,

    /// <summary>Verified, but the payload was not something this provider sends.</summary>
    Unreadable = 3,

    /// <summary>No such provider is registered here.</summary>
    UnknownProvider = 4,
}

/// <summary>The outcome of a callback.</summary>
/// <param name="Decision">What the endpoint should answer.</param>
/// <param name="ReceiptsCarried">How many outcomes it reported.</param>
/// <param name="ReceiptsApplied">How many of them moved a message.</param>
public sealed record CallbackResult(
    CallbackDecision Decision,
    int ReceiptsCarried,
    int ReceiptsApplied);

/// <summary>
/// Applies what a provider reported.
/// </summary>
/// <remarks>
/// The one write path an outside party can reach, which is why verification comes
/// first and everything after it assumes nothing (ADR 0013).
/// <para>
/// The shape that matters: <b>a receipt that changes nothing is a success</b>. A
/// duplicate, a receipt for a message the sweeper already abandoned, a receipt for
/// a message that was cancelled — all are acknowledged. The status code answers
/// "did you receive this?", and a provider that reads a 4xx resends, so reporting
/// a conflict for a duplicate causes the retries it appears to be complaining
/// about.
/// </para>
/// </remarks>
public sealed class ProcessCallbackHandler(
    ICallbackGateway gateway,
    IMessageRepository messages,
    ICallbackLog log,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<ProcessCallbackHandler> logger)
{
    /// <summary>Verifies a callback and applies whatever it reported.</summary>
    public async Task<CallbackResult> HandleAsync(
        CallbackSubmission submission,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();

        Result<ProviderId> providerId = ProviderId.Create(submission.ProviderId);

        if (providerId.IsFailure || !gateway.CanReceive(providerId.Value))
        {
            // Recorded even though nothing can be done with it. A stream of
            // callbacks for a provider this instance does not have means either a
            // stale configuration somewhere or someone guessing at endpoints, and
            // both are things worth being able to see.
            await log.RecordUnknownProviderAsync(submission, now, cancellationToken)
                .ConfigureAwait(false);

            logger.LogWarning(
                "A callback arrived for provider {ProviderId}, which is not registered here.",
                submission.ProviderId);

            return new CallbackResult(CallbackDecision.UnknownProvider, 0, 0);
        }

        CallbackVerification verification = gateway.Verify(providerId.Value, submission, now);

        if (verification.Verdict is not CallbackVerdictKind.Accepted)
        {
            await log.RecordFailedAsync(submission, verification, now, cancellationToken)
                .ConfigureAwait(false);

            // Logged at warning with the reason, because this is where a
            // misconfigured secret shows up — and the caller is told nothing, so
            // this log line is the only place the answer exists (ADR 0013).
            logger.LogWarning(
                "A callback from {ProviderId} was not accepted: {Verdict}. {Detail}",
                submission.ProviderId,
                verification.Verdict,
                verification.Detail);

            return new CallbackResult(
                verification.Verdict is CallbackVerdictKind.Malformed
                    ? CallbackDecision.Unreadable
                    : CallbackDecision.Refuse,
                0,
                0);
        }

        int applied = 0;

        foreach (DeliveryReport report in verification.Reports)
        {
            if (await ApplyAsync(providerId.Value, report, now, cancellationToken).ConfigureAwait(false))
            {
                applied++;
            }
        }

        await log.RecordAppliedAsync(
                submission,
                verification.Reports.Count,
                applied,
                now,
                cancellationToken)
            .ConfigureAwait(false);

        // One commit for the whole callback: the message changes, the events they
        // raised, and the audit row. A batch either lands or does not, so a
        // provider retrying after a partial failure cannot produce a half-applied
        // batch on top of a half-applied batch.
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new CallbackResult(
            CallbackDecision.Acknowledge,
            verification.Reports.Count,
            applied);
    }

    private async Task<bool> ApplyAsync(
        ProviderId provider,
        DeliveryReport report,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Message? message = await messages
            .FindByProviderMessageIdAsync(provider, report.ProviderMessageId, cancellationToken)
            .ConfigureAwait(false);

        if (message is null)
        {
            // A receipt for something Relay has no record of. Almost always a
            // callback for a message that was pruned, or a provider replaying an
            // old batch — not an error, and specifically not worth failing the
            // whole callback over.
            logger.LogInformation(
                "A receipt from {ProviderId} named message {ProviderMessageId}, which is unknown here.",
                provider.Value,
                report.ProviderMessageId);

            return false;
        }

        Result outcome = report.Status switch
        {
            DeliveryReportStatus.Delivered => message.ConfirmDelivered(report.OccurredAt ?? now),
            DeliveryReportStatus.Failed => message.ConfirmFailed(report.Reason, report.OccurredAt ?? now),
            _ => Result.Success(),
        };

        if (outcome.IsSuccess)
        {
            return true;
        }

        // The message was already terminal — abandoned by the sweeper, cancelled,
        // or delivered. The receipt is true and arrives too late to act on, and it
        // is left in the audit log rather than applied: something has already
        // reported this message's outcome, and reversing that quietly would make
        // the earlier report a lie (ADR 0008).
        logger.LogInformation(
            "A late receipt for message {MessageId} was recorded but not applied: {Error}.",
            message.Id.Value,
            outcome.Error.Code);

        return false;
    }
}
