namespace Relay.Infrastructure.Callbacks;

/// <summary>How a callback was handled.</summary>
public enum CallbackDisposition
{
    /// <summary>Unset.</summary>
    None = 0,

    /// <summary>Verified, and it moved at least one message.</summary>
    Applied = 1,

    /// <summary>
    /// Verified, and it changed nothing.
    /// </summary>
    /// <remarks>
    /// A duplicate, a receipt for a message already terminal, or an event type
    /// that says nothing about delivery. Not a failure — and the most common
    /// outcome once a provider starts retrying.
    /// </remarks>
    NoOp = 2,

    /// <summary>The signature or timestamp did not hold up.</summary>
    Rejected = 3,

    /// <summary>Verified, but the payload was not something the provider sends.</summary>
    Malformed = 4,

    /// <summary>Addressed to a provider this instance does not have.</summary>
    UnknownProvider = 5,
}

/// <summary>
/// A callback that arrived, and what became of it.
/// </summary>
/// <remarks>
/// Written for every inbound callback, including the ones that changed nothing
/// and the ones that were rejected. That is the point: after an incident the
/// question is "did the provider tell us, and when?", and the answer is only
/// available if the callbacks that did nothing were recorded too.
/// <para>
/// A log that only contains successes cannot distinguish "the provider never
/// told us" from "the provider told us and we threw it away", which are very
/// different conversations to have with a vendor.
/// </para>
/// <para>
/// Immutable once written. This is an audit record, not state — nothing updates
/// a row here, and the disposition is decided before the insert.
/// </para>
/// </remarks>
public sealed class CallbackRecord
{
    private CallbackRecord(
        Guid id,
        string providerId,
        CallbackDisposition disposition,
        string? detail,
        int receiptCount,
        int appliedCount,
        string bodyPreview,
        DateTimeOffset receivedAt)
    {
        Id = id;
        ProviderId = providerId;
        Disposition = disposition;
        Detail = detail;
        ReceiptCount = receiptCount;
        AppliedCount = appliedCount;
        BodyPreview = bodyPreview;
        ReceivedAt = receivedAt;
    }

    private CallbackRecord()
    {
        ProviderId = null!;
        BodyPreview = null!;
    }

    /// <summary>Identity.</summary>
    public Guid Id { get; private init; }

    /// <summary>Which provider the callback claimed to be from.</summary>
    /// <remarks>
    /// Claimed, not proven, for rejected records — a forged callback names
    /// whatever provider the sender chose. That is worth keeping: a burst of
    /// rejected callbacks naming one provider is the shape of someone probing.
    /// </remarks>
    public string ProviderId { get; private init; }

    /// <summary>What happened to it.</summary>
    public CallbackDisposition Disposition { get; private init; }

    /// <summary>Why, for a rejection or a malformed payload.</summary>
    public string? Detail { get; private init; }

    /// <summary>How many receipts it carried.</summary>
    public int ReceiptCount { get; private init; }

    /// <summary>How many of them changed a message.</summary>
    /// <remarks>
    /// Separate from <see cref="ReceiptCount"/> because a batch is routinely
    /// mostly duplicates. A callback carrying fifty receipts that applied none is
    /// normal; one carrying fifty that applied fifty twice is not.
    /// </remarks>
    public int AppliedCount { get; private init; }

    /// <summary>
    /// The first part of the body.
    /// </summary>
    /// <remarks>
    /// Truncated rather than stored whole. The full payload of every callback
    /// would dominate the database within weeks, and the diagnostic value is in
    /// the first few hundred characters — which provider, which event, which
    /// message.
    /// <para>
    /// Callback bodies carry recipient addresses, so this is personal data with a
    /// retention obligation. Pruning is a scheduled job rather than something this
    /// type knows about, but the truncation is here so the volume is bounded from
    /// the first row.
    /// </para>
    /// </remarks>
    public string BodyPreview { get; private init; }

    /// <summary>When it arrived.</summary>
    public DateTimeOffset ReceivedAt { get; private init; }

    /// <summary>Records a callback.</summary>
    public static CallbackRecord Of(
        string providerId,
        CallbackDisposition disposition,
        string? detail,
        int receiptCount,
        int appliedCount,
        string body,
        DateTimeOffset receivedAt) =>
        new(
            Guid.CreateVersion7(),
            providerId,
            disposition,
            Truncate(detail, 1_000),
            receiptCount,
            appliedCount,
            Truncate(body, 2_000) ?? string.Empty,
            receivedAt);

    private static string? Truncate(string? value, int limit) =>
        value is null || value.Length <= limit ? value : string.Concat(value.AsSpan(0, limit), "…");
}
