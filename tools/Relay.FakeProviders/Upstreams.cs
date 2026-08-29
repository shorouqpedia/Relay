using System.Collections.Concurrent;

namespace Relay.FakeProviders;

/// <summary>How a fake upstream should respond.</summary>
public enum FakeBehaviour
{
    /// <summary>Respond normally and report delivery afterwards.</summary>
    Accept = 0,

    /// <summary>Refuse permanently.</summary>
    Reject = 1,

    /// <summary>Refuse for quota reasons.</summary>
    RateLimit = 2,

    /// <summary>Fail with a server-side error.</summary>
    ServerError = 3,

    /// <summary>Accept the connection and never answer.</summary>
    Hang = 4,

    /// <summary>
    /// Accept the message, and then never report anything about it.
    /// </summary>
    /// <remarks>
    /// The most useful behaviour here, and the one that is impossible to arrange
    /// with a real provider. It produces the condition the reconciliation sweeper
    /// exists for: a message that is <c>Sent</c> forever because the thing that
    /// would move it is silence.
    /// </remarks>
    Silent = 5,
}

/// <summary>
/// What each upstream is currently doing.
/// </summary>
/// <remarks>
/// In memory and per process, which is right for a development tool: the point is
/// to change behaviour from a terminal and watch the system react, not to survive
/// a restart.
/// </remarks>
public sealed class UpstreamBehaviourStore
{
    private readonly ConcurrentDictionary<string, FakeBehaviour> _behaviours =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The behaviour for an upstream, defaulting to accepting.</summary>
    public FakeBehaviour Get(string upstream) =>
        _behaviours.TryGetValue(upstream, out FakeBehaviour behaviour) ? behaviour : FakeBehaviour.Accept;

    /// <summary>Sets how an upstream will respond from now on.</summary>
    public void Set(string upstream, FakeBehaviour behaviour) => _behaviours[upstream] = behaviour;

    /// <summary>Every upstream that has been configured, and how.</summary>
    public IReadOnlyDictionary<string, string> All() =>
        _behaviours.ToDictionary(pair => pair.Key, pair => pair.Value.ToString());
}

/// <summary>A delivery receipt waiting to be sent back to Relay.</summary>
/// <param name="Upstream">Which fake upstream is reporting.</param>
/// <param name="CallbackUrl">Where to send it.</param>
/// <param name="ProviderMessageId">The identifier this upstream returned when it accepted the message.</param>
/// <param name="DueAt">When to send it — a few seconds later, as a real provider would.</param>
/// <param name="Silent">When set, the receipt is dropped rather than sent.</param>
public sealed record PendingReceipt(
    string Upstream,
    string CallbackUrl,
    string ProviderMessageId,
    DateTimeOffset DueAt,
    bool Silent);

/// <summary>Receipts the fake upstreams have promised to send.</summary>
/// <remarks>
/// Named for what it holds rather than for how it holds it. A <c>Queue</c> suffix
/// on a type that is not a collection tells a reader it can be enumerated and
/// indexed, which this cannot.
/// </remarks>
public sealed class PendingReceipts
{
    private readonly ConcurrentQueue<PendingReceipt> _queue = new();

    /// <summary>Promises a receipt.</summary>
    public void Enqueue(PendingReceipt receipt) => _queue.Enqueue(receipt);

    /// <summary>Takes the receipts that are due.</summary>
    public IReadOnlyList<PendingReceipt> TakeDue(DateTimeOffset now)
    {
        List<PendingReceipt> due = [];

        // Drains and re-enqueues rather than peeking, because the queue is not
        // ordered by due time — receipts have different delays per upstream, so
        // the head is not necessarily the earliest.
        int count = _queue.Count;

        for (int i = 0; i < count && _queue.TryDequeue(out PendingReceipt? receipt); i++)
        {
            if (receipt.DueAt <= now)
            {
                due.Add(receipt);
            }
            else
            {
                _queue.Enqueue(receipt);
            }
        }

        return due;
    }
}
