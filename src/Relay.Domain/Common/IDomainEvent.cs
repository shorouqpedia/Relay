namespace Relay.Domain.Common;

/// <summary>
/// A fact about something that has already happened inside the domain.
/// </summary>
/// <remarks>
/// Domain events are raised by aggregates during a state transition and collected
/// by the unit of work, which publishes them through the outbox in the same
/// transaction as the state change (ADR 0008). They are past tense by convention
/// because they describe what occurred, not what should occur next.
/// </remarks>
public interface IDomainEvent
{
    /// <summary>When the event occurred, in UTC.</summary>
    DateTimeOffset OccurredAt { get; }
}
