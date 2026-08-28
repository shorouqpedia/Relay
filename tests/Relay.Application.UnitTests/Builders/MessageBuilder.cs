using Relay.Domain.Common;
using Relay.Domain.Messaging;

namespace Relay.Application.UnitTests.Builders;

/// <summary>
/// Builds a <see cref="Message"/> in a chosen lifecycle state.
/// </summary>
/// <remarks>
/// A second copy of the builder in the domain test project, and deliberately not
/// shared with it. A shared test helper becomes a dependency between two suites:
/// a change one of them needs starts breaking the other, and the helper grows
/// options nobody can delete because they cannot tell who relies on them.
/// <para>
/// This one is smaller and exposes only what routing tests need. The duplication
/// is a few dozen lines and it keeps each suite free to change.
/// </para>
/// </remarks>
internal sealed class MessageBuilder
{
    private static readonly DateTimeOffset DefaultNow = new(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);

    private ChannelType _channel = ChannelType.Email;
    private string _address = "recipient@example.com";
    private int _maxAttempts = 3;

    public MessageBuilder OnChannel(ChannelType channel, string address)
    {
        _channel = channel;
        _address = address;
        return this;
    }

    public MessageBuilder WithMaxAttempts(int maxAttempts)
    {
        _maxAttempts = maxAttempts;
        return this;
    }

    /// <summary>Builds a message in <see cref="MessageStatus.Pending"/>.</summary>
    public Message Build() => Expect(Message.Submit(
        Expect(IdempotencyKey.Create($"idem-{Guid.CreateVersion7():N}")),
        Expect(Recipient.Create(_channel, _address)),
        Expect(MessageBody.Create(_channel, "Body text.", _channel is ChannelType.Email ? "Subject" : null)),
        _maxAttempts,
        DefaultNow));

    /// <summary>
    /// Builds a message that has failed once on <paramref name="provider"/> and is
    /// pending again.
    /// </summary>
    /// <remarks>
    /// Walked through the real transitions rather than assembled. Setup that
    /// cannot produce an illegal state cannot give a test a false premise.
    /// </remarks>
    public Message BuildAfterFailedAttempt(string provider)
    {
        Message message = Build();

        Expect(message.BeginDispatch(Expect(ProviderId.Create(provider)), DefaultNow));
        Expect(message.RecordAttempt(
            AttemptOutcome.TransientFailure,
            providerMessageId: null,
            failureReason: "upstream unavailable",
            duration: TimeSpan.FromMilliseconds(50),
            DefaultNow));

        return message;
    }

    /// <summary>Builds a message a provider has accepted.</summary>
    public Message BuildSent(string provider = "email.postal", string providerMessageId = "pm-1")
    {
        Message message = Build();

        Expect(message.BeginDispatch(Expect(ProviderId.Create(provider)), DefaultNow));
        Expect(message.RecordAttempt(
            AttemptOutcome.Accepted,
            providerMessageId,
            failureReason: null,
            duration: TimeSpan.FromMilliseconds(80),
            DefaultNow));

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
