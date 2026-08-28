using Microsoft.Extensions.Logging;
using Relay.Domain.Common;
using Relay.Domain.Messaging;

namespace Relay.Application.Delivery;

/// <summary>
/// Resolves messages a provider accepted and then never mentioned again.
/// </summary>
/// <remarks>
/// This handles the only failure mode that produces no event: nothing happened.
/// A message sits in <see cref="MessageStatus.Sent"/> because the receipt was
/// lost, or the provider never sends one, or the provider has quietly forgotten
/// the message. None of those can be subscribed to, so they have to be looked for
/// (ADR 0011).
/// <para>
/// Where the provider can be asked, it is asked, and the answer is applied. Where
/// it cannot, the message is abandoned — recorded as dead-lettered with the
/// reason that Relay stopped waiting, which is a different and more honest claim
/// than saying delivery failed.
/// </para>
/// </remarks>
public sealed class ReceiptReconciler(
    IMessageRepository messages,
    IProviderRegistry registry,
    IReceiptQuery receipts,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<ReceiptReconciler> logger)
{
    /// <summary>
    /// Reconciles one batch of overdue messages.
    /// </summary>
    /// <returns>How many messages reached a terminal state.</returns>
    public async Task<int> ReconcileAsync(int batchSize, CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();

        // The cutoff is the longest window any provider declares, not a global
        // constant. Providers differ by orders of magnitude — an SMS receipt
        // arrives in seconds, an email bounce can take hours — so one number would
        // either abandon healthy messages or wait forever on dead ones.
        TimeSpan longestWindow = registry.All.Count == 0
            ? TimeSpan.FromHours(1)
            : registry.All.Max(p => p.ExpectedReceiptWindow);

        IReadOnlyList<Message> overdue = await messages
            .FindAwaitingReceiptAsync(now - longestWindow, batchSize, cancellationToken)
            .ConfigureAwait(false);

        if (overdue.Count == 0)
        {
            return 0;
        }

        int resolved = 0;

        foreach (Message message in overdue)
        {
            if (await ReconcileOneAsync(message, now, cancellationToken).ConfigureAwait(false))
            {
                resolved++;
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return resolved;
    }

    private async Task<bool> ReconcileOneAsync(
        Message message,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ProviderProfile? provider = message.CurrentProviderId is { } id ? registry.Find(id) : null;

        if (provider is null)
        {
            // The provider that sent this message is no longer registered — it was
            // removed between the send and now. Nothing can resolve it.
            return Abandon(message, "the provider that sent this message is no longer registered", now);
        }

        // Per-provider window, checked here rather than in the query. The query
        // uses the longest window so one pass covers every provider; this is where
        // a message that is overdue for a slow provider but not for a fast one is
        // put back.
        if (message.SentAt is { } sentAt && now - sentAt < provider.ExpectedReceiptWindow)
        {
            return false;
        }

        string? providerMessageId = message.Attempts
            .LastOrDefault(a => a.Outcome == AttemptOutcome.Accepted)
            ?.ProviderMessageId;

        if (providerMessageId is null)
        {
            // The provider accepted without returning an identifier, so there is
            // nothing to ask about. This is the concrete cost of a provider that
            // does not declare ReturnsMessageId.
            return Abandon(message, "the provider returned no message id, so its status cannot be queried", now);
        }

        ReceiptStatus status = await receipts
            .QueryAsync(provider.Id, providerMessageId, cancellationToken)
            .ConfigureAwait(false);

        return status switch
        {
            ReceiptStatus.Delivered => Apply(message, message.ConfirmDelivered(now), "delivered"),
            ReceiptStatus.Failed => Apply(
                message,
                message.ConfirmFailed("the provider reported this message as undeliverable", now),
                "failed"),

            // Still in flight as far as the provider is concerned. Left alone: the
            // window is an estimate, and the provider's own answer beats it.
            ReceiptStatus.Pending => false,

            // The provider has no record of it. Almost always means the message
            // aged out of its retention window rather than that it never existed,
            // so it is evidence of nothing except that asking again is pointless.
            ReceiptStatus.Unknown or ReceiptStatus.None => Abandon(
                message,
                "the provider has no record of this message",
                now),

            _ => false,
        };
    }

    private bool Apply(Message message, Result outcome, string what)
    {
        if (outcome.IsSuccess)
        {
            return true;
        }

        // The message reached a terminal state between the query being sent and
        // the answer arriving — a pushed receipt won the race. Nothing to do, and
        // nothing wrong.
        logger.LogDebug(
            "Message {MessageId} could not be marked {What}: {Error}.",
            message.Id.Value,
            what,
            outcome.Error.Code);

        return false;
    }

    private bool Abandon(Message message, string reason, DateTimeOffset now)
    {
        Result outcome = message.Abandon(reason, now);

        if (outcome.IsFailure)
        {
            return false;
        }

        logger.LogWarning(
            "Message {MessageId} abandoned after its receipt window elapsed: {Reason}.",
            message.Id.Value,
            reason);

        return true;
    }
}

/// <summary>
/// Asks a provider what became of a message, where the provider supports it.
/// </summary>
/// <remarks>
/// The application-side counterpart of the providers' <c>IReceiptQueryable</c>,
/// existing for the same reason as <see cref="IDeliveryGateway"/>: Application
/// cannot reference the provider assemblies.
/// </remarks>
public interface IReceiptQuery
{
    /// <summary>
    /// Asks about one message.
    /// </summary>
    /// <returns>
    /// <see cref="ReceiptStatus.Unknown"/> when the provider cannot be asked at
    /// all, which is deliberately the same answer as "the provider does not know".
    /// Both mean the same thing to the caller: no more information is coming.
    /// </returns>
    Task<ReceiptStatus> QueryAsync(
        ProviderId provider,
        string providerMessageId,
        CancellationToken cancellationToken);
}

/// <summary>What a provider says about a message it accepted.</summary>
/// <remarks>
/// Mirrors the enum in the providers assembly. The duplication is the cost of the
/// dependency rule, and it is bounded: this is a closed set that changes only when
/// the contract itself does.
/// </remarks>
public enum ReceiptStatus
{
    /// <summary>Unset.</summary>
    None = 0,

    /// <summary>Still in flight.</summary>
    Pending = 1,

    /// <summary>The recipient received it.</summary>
    Delivered = 2,

    /// <summary>The provider could not deliver it.</summary>
    Failed = 3,

    /// <summary>The provider has no record of it, or cannot be asked.</summary>
    Unknown = 4,
}
