namespace Relay.Domain.Messaging;

/// <summary>
/// Where a message is in its lifecycle.
/// </summary>
/// <remarks>
/// The legal transitions are:
/// <code>
///   Pending ─────► Dispatching ─────► Sent ─────► Delivered
///      │                │               │
///      │                │               └───────► Failed
///      │                └───────────────────────► Pending      (retry)
///      │                                    └────► DeadLettered
///      └────────────────────────────────────────► Cancelled
/// </code>
/// <para>
/// <see cref="Delivered"/>, <see cref="Failed"/>, <see cref="DeadLettered"/>, and
/// <see cref="Cancelled"/> are terminal: nothing moves out of them. That is what
/// makes a late delivery receipt safe to receive — it is recorded as a fact
/// without moving the message anywhere.
/// </para>
/// <para>
/// The distinction between <see cref="Sent"/> and <see cref="Delivered"/> is the
/// one that matters operationally. Sent means a provider accepted the message.
/// Delivered means the recipient received it. Providers that never report the
/// second leave messages sitting in <see cref="Sent"/>, which is why the
/// reconciliation sweeper exists rather than being an optimisation.
/// </para>
/// </remarks>
public enum MessageStatus
{
    /// <summary>Unset. Present so that a default-initialised value is invalid rather than meaningful.</summary>
    None = 0,

    /// <summary>Accepted by Relay, not yet handed to a provider.</summary>
    Pending = 1,

    /// <summary>Currently being handed to a provider.</summary>
    Dispatching = 2,

    /// <summary>A provider accepted it. Whether the recipient got it is not yet known.</summary>
    Sent = 3,

    /// <summary>Confirmed received by the recipient. Terminal.</summary>
    Delivered = 4,

    /// <summary>A provider rejected it in a way that will not change on retry. Terminal.</summary>
    Failed = 5,

    /// <summary>Retries were exhausted without a definite outcome. Terminal.</summary>
    DeadLettered = 6,

    /// <summary>Withdrawn by the caller before dispatch. Terminal.</summary>
    Cancelled = 7,
}
