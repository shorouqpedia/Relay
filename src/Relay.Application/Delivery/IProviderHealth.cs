using Relay.Domain.Messaging;

namespace Relay.Application.Delivery;

/// <summary>
/// Whether a provider is worth trying right now.
/// </summary>
/// <remarks>
/// Separate from <see cref="IProviderRegistry"/> because the two answer questions
/// with different lifetimes: which providers exist never changes after startup,
/// and whether one is currently working changes by the second.
/// <para>
/// This is what makes failover possible. Without it the router would keep
/// choosing the highest-priority provider through an outage, and every message
/// would spend its whole retry budget on the one upstream that is known to be
/// down.
/// </para>
/// </remarks>
public interface IProviderHealth
{
    /// <summary>Whether the provider should be offered work.</summary>
    bool IsAvailable(ProviderId provider);

    /// <summary>
    /// Records what happened on an attempt, so future routing can use it.
    /// </summary>
    /// <param name="provider">The provider that was tried.</param>
    /// <param name="outcome">How the attempt ended.</param>
    /// <param name="retryAfter">
    /// How long the provider asked to be left alone, when it said. Honoured
    /// exactly: a provider that reports a delay and is ignored escalates from
    /// refusing requests to blocking the account.
    /// </param>
    void Record(ProviderId provider, AttemptOutcome outcome, TimeSpan? retryAfter = null);
}
