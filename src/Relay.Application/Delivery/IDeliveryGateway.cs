using Relay.Domain.Messaging;

namespace Relay.Application.Delivery;

/// <summary>
/// The application's view of "hand this message to that provider".
/// </summary>
/// <remarks>
/// Exists because Application cannot reference the provider assemblies, and must
/// not: the core knowing which providers exist is precisely what ADR 0003 removes.
/// The host implements this over the decorated <c>IMessageProvider</c> instances,
/// resolving one by id.
/// <para>
/// The shape mirrors the provider contract on purpose, including the rule that it
/// does not throw. A gateway that converted provider failures into exceptions
/// would put the pipeline back in the position of catching things it cannot
/// enumerate.
/// </para>
/// </remarks>
public interface IDeliveryGateway
{
    /// <summary>
    /// Attempts delivery through one provider.
    /// </summary>
    /// <remarks>
    /// Does not throw except for <see cref="OperationCanceledException"/>.
    /// </remarks>
    Task<DeliveryOutcome> SendAsync(
        ProviderId provider,
        Message message,
        CancellationToken cancellationToken);
}

/// <summary>
/// What a provider made of a delivery attempt, in the application's vocabulary.
/// </summary>
/// <param name="Outcome">How the attempt ended.</param>
/// <param name="ProviderMessageId">The provider's identifier for the message, when it gave one.</param>
/// <param name="FailureReason">Why it failed, already masked by the provider.</param>
/// <param name="RetryAfter">How long the provider asked to be left alone, when it said.</param>
/// <param name="Duration">How long the physical call took.</param>
public sealed record DeliveryOutcome(
    AttemptOutcome Outcome,
    string? ProviderMessageId,
    string? FailureReason,
    TimeSpan? RetryAfter,
    TimeSpan Duration);
