using Relay.Domain.Messaging;

namespace Relay.Application.Callbacks;

/// <summary>What a provider said became of a message.</summary>
public enum DeliveryReportStatus
{
    /// <summary>Unset.</summary>
    None = 0,

    /// <summary>The recipient received it.</summary>
    Delivered = 1,

    /// <summary>The provider could not deliver it.</summary>
    Failed = 2,
}

/// <summary>One delivery outcome from a callback.</summary>
/// <param name="ProviderMessageId">The provider's identifier for the message.</param>
/// <param name="Status">What it says happened.</param>
/// <param name="Reason">Why, where the provider said.</param>
/// <param name="OccurredAt">
/// When the provider says it happened. Null when it did not say, in which case the
/// handler falls back to now — a receipt with no timestamp is still worth applying,
/// and refusing it would discard true information over a missing field.
/// </param>
public sealed record DeliveryReport(
    string ProviderMessageId,
    DeliveryReportStatus Status,
    string? Reason,
    DateTimeOffset? OccurredAt);

/// <summary>Whether a callback may be acted on.</summary>
public enum CallbackVerdictKind
{
    /// <summary>Unset.</summary>
    None = 0,

    /// <summary>Signed correctly, recent, and understood.</summary>
    Accepted = 1,

    /// <summary>The signature or timestamp did not hold up.</summary>
    Rejected = 2,

    /// <summary>Correctly signed, but not a payload this provider sends.</summary>
    Malformed = 3,
}

/// <summary>The result of verifying a callback.</summary>
/// <param name="Verdict">Whether it may be acted on.</param>
/// <param name="Reports">What it reported, when accepted.</param>
/// <param name="Detail">Why it was refused. For Relay's logs only — never returned to the caller.</param>
public sealed record CallbackVerification(
    CallbackVerdictKind Verdict,
    IReadOnlyList<DeliveryReport> Reports,
    string? Detail);

/// <summary>
/// Verifies callbacks using the right provider's rules.
/// </summary>
/// <remarks>
/// The application's view of provider callback handling, mirroring
/// <c>IDeliveryGateway</c> and existing for the same reason: Application
/// cannot reference the provider assemblies, so the host translates.
/// <para>
/// Verification is per provider because every provider signs differently — a
/// different header, a different hash encoding, a different string to sign. A
/// single shared implementation would be a switch on provider id, which is the
/// coupling the plugin boundary removes (ADR 0003).
/// </para>
/// </remarks>
public interface ICallbackGateway
{
    /// <summary>Whether a provider is registered here and reports delivery by callback.</summary>
    bool CanReceive(ProviderId provider);

    /// <summary>
    /// Verifies a callback and extracts what it reports.
    /// </summary>
    /// <remarks>Does not throw: the endpoint is public, so anything postable will be posted.</remarks>
    CallbackVerification Verify(ProviderId provider, CallbackSubmission submission, DateTimeOffset now);
}

/// <summary>
/// Records every callback that arrives.
/// </summary>
/// <remarks>
/// A port rather than a repository method, because the audit log is not part of
/// any aggregate — it is a flat, append-only record with no invariants to enforce
/// and no consistency boundary to belong to. Making it an aggregate would be the
/// DDD machinery applied where the domain does not call for it.
/// </remarks>
public interface ICallbackLog
{
    /// <summary>Records a callback that was verified and applied.</summary>
    Task RecordAppliedAsync(
        CallbackSubmission submission,
        int carried,
        int applied,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken);

    /// <summary>Records a callback that failed verification or could not be read.</summary>
    Task RecordFailedAsync(
        CallbackSubmission submission,
        CallbackVerification verification,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records a callback for a provider that is not registered here.
    /// </summary>
    /// <remarks>
    /// Committed immediately rather than waiting for the unit of work, because
    /// this path returns without a save — and an audit row that is only written
    /// when something else succeeds is not an audit row.
    /// </remarks>
    Task RecordUnknownProviderAsync(
        CallbackSubmission submission,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken);
}
