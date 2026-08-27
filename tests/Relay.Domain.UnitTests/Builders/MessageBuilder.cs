using Relay.Domain.Common;
using Relay.Domain.Messaging;

namespace Relay.Domain.UnitTests.Builders;

/// <summary>
/// Builds a <see cref="Message"/> in a chosen lifecycle state.
/// </summary>
/// <remarks>
/// A rich aggregate cannot be object-initialised into an arbitrary state, so a
/// test that needs a sent message has to walk one there. Doing that inline in
/// every test would bury the assertion under four lines of setup and would couple
/// every test to the exact transition sequence.
/// <para>
/// The builder walks the real transitions rather than reaching past them. That is
/// deliberate: setup that cannot produce an illegal state is setup that cannot
/// give a test a false premise. It also means these methods throw on an illegal
/// sequence — a test asking for a state the domain cannot reach is a broken test,
/// and it should say so at the point of the mistake.
/// </para>
/// </remarks>
internal sealed class MessageBuilder
{
    private static readonly DateTimeOffset DefaultNow =
        new(2026, 1, 15, 9, 0, 0, TimeSpan.Zero);

    private ChannelType _channel = ChannelType.Email;
    private string _address = "recipient@example.com";
    private string _content = "A message body.";
    private string? _subject;
    private string _idempotencyKey = "idem-key-0001";
    private int _maxAttempts = 3;
    private DateTimeOffset _now = DefaultNow;

    public MessageBuilder OnChannel(ChannelType channel, string address)
    {
        _channel = channel;
        _address = address;
        return this;
    }

    public MessageBuilder WithBody(string content, string? subject = null)
    {
        _content = content;
        _subject = subject;
        return this;
    }

    public MessageBuilder WithIdempotencyKey(string key)
    {
        _idempotencyKey = key;
        return this;
    }

    public MessageBuilder WithMaxAttempts(int maxAttempts)
    {
        _maxAttempts = maxAttempts;
        return this;
    }

    public MessageBuilder At(DateTimeOffset now)
    {
        _now = now;
        return this;
    }

    /// <summary>Builds a message in <see cref="MessageStatus.Pending"/>.</summary>
    public Message Build()
    {
        IdempotencyKey key = Expect(IdempotencyKey.Create(_idempotencyKey));
        Recipient recipient = Expect(Recipient.Create(_channel, _address));
        MessageBody body = Expect(MessageBody.Create(_channel, _content, _subject));

        return Expect(Message.Submit(key, recipient, body, _maxAttempts, _now));
    }

    /// <summary>Builds a message claimed for dispatch by <paramref name="provider"/>.</summary>
    public Message BuildDispatching(string provider = "email.postal")
    {
        Message message = Build();
        Expect(message.BeginDispatch(Expect(ProviderId.Create(provider)), _now));
        return message;
    }

    /// <summary>Builds a message a provider has accepted.</summary>
    public Message BuildSent(string provider = "email.postal", string? providerMessageId = "pm-1")
    {
        Message message = BuildDispatching(provider);
        Expect(message.RecordAttempt(
            AttemptOutcome.Accepted,
            providerMessageId,
            failureReason: null,
            duration: TimeSpan.FromMilliseconds(120),
            _now));
        return message;
    }

    /// <summary>Builds a message confirmed as received.</summary>
    public Message BuildDelivered(string provider = "email.postal")
    {
        Message message = BuildSent(provider);
        Expect(message.ConfirmDelivered(_now));
        return message;
    }

    /// <summary>
    /// Builds a message whose retry budget is spent, leaving it dead-lettered.
    /// </summary>
    public Message BuildDeadLettered(string provider = "email.postal")
    {
        Message message = Build();
        ProviderId providerId = Expect(ProviderId.Create(provider));

        for (int attempt = 0; attempt < _maxAttempts; attempt++)
        {
            Expect(message.BeginDispatch(providerId, _now));
            Expect(message.RecordAttempt(
                AttemptOutcome.TransientFailure,
                providerMessageId: null,
                failureReason: "upstream unavailable",
                duration: TimeSpan.FromMilliseconds(50),
                _now));
        }

        return message;
    }

    /// <summary>Builds a message withdrawn before dispatch.</summary>
    public Message BuildCancelled()
    {
        Message message = Build();
        Expect(message.Cancel(_now));
        return message;
    }

    private static T Expect<T>(Result<T> result) =>
        result.IsSuccess
            ? result.Value
            : throw new InvalidOperationException(
                $"Builder setup produced a failure the domain rejected: {result.Error}. "
                + "The test is asking for a state that cannot legally exist.");

    private static void Expect(Result result)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException(
                $"Builder setup produced a failure the domain rejected: {result.Error}. "
                + "The test is asking for a state that cannot legally exist.");
        }
    }
}
