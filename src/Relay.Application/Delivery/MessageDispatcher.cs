using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Relay.Application.Observability;
using Relay.Domain.Common;
using Relay.Domain.Messaging;

namespace Relay.Application.Delivery;

/// <summary>
/// Takes one message from <see cref="MessageStatus.Pending"/> to a provider and
/// records what happened.
/// </summary>
/// <remarks>
/// One message per call, and the caller commits. That split matters: the unit of
/// work has to close around a single message so that one failing delivery does
/// not roll back the outcomes of every other message in the batch.
/// <para>
/// The ordering inside the dispatch method is the design. The claim is
/// committed <em>before</em> the provider is called, so that a process dying
/// during the call leaves a row in <see cref="MessageStatus.Dispatching"/> that
/// the recovery loop can find. Calling first and recording afterwards would be
/// one fewer round trip and would lose the message entirely on a crash — it would
/// sit in <see cref="MessageStatus.Pending"/>, having already been sent, and be
/// sent again by the next worker with nothing recording that it had been.
/// </para>
/// </remarks>
public sealed class MessageDispatcher(
    ProviderRouter router,
    IDeliveryGateway gateway,
    IProviderHealth health,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<MessageDispatcher> logger)
{
    /// <summary>
    /// Dispatches one claimed message.
    /// </summary>
    /// <remarks>
    /// Takes a <see cref="ClaimedMessage"/> rather than a bare
    /// <see cref="Message"/>, so the trace the message was submitted under travels
    /// with it. The span started here is a <em>new trace with a link</em> back to
    /// that submission, not a continuation of it: the request that accepted the
    /// message finished long ago — possibly hours — and making its span the parent
    /// would produce a trace with no end and a duration that destroys any latency
    /// percentile computed over it (ADR 0014).
    /// </remarks>
    /// <returns>
    /// The outcome recorded, or a failure when no provider could be chosen. A
    /// failure here is not an error condition — it usually means every provider
    /// for the channel is circuit-broken, and the message stays pending.
    /// </returns>
    public async Task<Result<AttemptOutcome>> DispatchAsync(
        ClaimedMessage claimed,
        CancellationToken cancellationToken)
    {
        Message message = claimed.Message;

        using Activity? activity = RelayTelemetry.StartLinked(
            RelayTelemetry.Spans.Dispatch,
            claimed.SubmissionTraceParent,
            ActivityKind.Client);

        activity?.SetTag(RelayTelemetry.Tags.MessageId, message.Id.Value);
        activity?.SetTag(RelayTelemetry.Tags.Channel, message.Channel.ToString());
        activity?.SetTag(RelayTelemetry.Tags.Attempt, message.AttemptCount + 1);

        Result<ProviderProfile> routed = router.Route(message);

        if (routed.IsFailure)
        {
            // Not an error span. Every provider being circuit-broken is a
            // condition the system handles by leaving the message pending, and
            // marking it as an error would fill an error dashboard with a state
            // that resolves itself.
            activity?.SetTag(RelayTelemetry.Tags.Outcome, routed.Error.Code);

            return Result<AttemptOutcome>.Failure(routed.Error);
        }

        ProviderProfile provider = routed.Value;

        activity?.SetTag(RelayTelemetry.Tags.Provider, provider.Id.Value);

        Result beganDispatch = message.BeginDispatch(provider.Id, clock.GetUtcNow());

        if (beganDispatch.IsFailure)
        {
            // The message moved under us between being claimed from the database
            // and reaching here — cancelled, or picked up by another worker whose
            // commit landed first. Not an error: the other writer won, correctly.
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(
                    "Message {MessageId} was no longer dispatchable: {Error}.",
                    message.Id.Value,
                    beganDispatch.Error.Code);
            }

            return Result<AttemptOutcome>.Failure(beganDispatch.Error);
        }

        // Committed before the call. This is the write that makes a crash during
        // delivery recoverable rather than invisible.
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        DeliveryOutcome outcome = await gateway
            .SendAsync(provider.Id, message, cancellationToken)
            .ConfigureAwait(false);

        health.Record(provider.Id, outcome.Outcome, outcome.RetryAfter);

        Result recorded = message.RecordAttempt(
            outcome.Outcome,
            outcome.ProviderMessageId,
            outcome.FailureReason,
            outcome.Duration,
            clock.GetUtcNow());

        if (recorded.IsFailure)
        {
            // The aggregate refused to record an outcome for a dispatch it
            // believes it is in the middle of. That is a contradiction rather than
            // a routine failure, and it is logged loudly because it means either
            // the state machine or this method is wrong.
            logger.LogError(
                "Message {MessageId} refused to record a {Outcome} attempt on {ProviderId}: {Error}.",
                message.Id,
                outcome.Outcome,
                provider.Id,
                recorded.Error.Code);

            return Result<AttemptOutcome>.Failure(recorded.Error);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        activity?.SetTag(RelayTelemetry.Tags.Outcome, outcome.Outcome.ToString());
        activity?.SetTag(RelayTelemetry.Tags.Status, message.Status.ToString());
        activity?.SetTag(RelayTelemetry.Tags.ProviderMessageId, outcome.ProviderMessageId);

        logger.LogInformation(
            "Message {MessageId} attempt {Attempt} on {ProviderId} ended {Outcome}; now {Status}.",
            message.Id.Value,
            message.AttemptCount,
            provider.Id.Value,
            outcome.Outcome,
            message.Status);

        return outcome.Outcome;
    }
}
