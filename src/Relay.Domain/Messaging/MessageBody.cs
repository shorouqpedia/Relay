using Relay.Domain.Common;

namespace Relay.Domain.Messaging;

/// <summary>
/// What the recipient will see, sized to what the channel can carry.
/// </summary>
/// <remarks>
/// Length limits belong here rather than in a validator because they are a
/// property of the channel, not of any particular request. A body that is too
/// long for SMS is too long no matter which endpoint or job produced it, and a
/// rule enforced at one entry point is a rule the next entry point will miss.
/// </remarks>
public sealed class MessageBody : ValueObject
{
    /// <summary>Single-segment GSM-7 is 160; concatenated SMS is billed per segment and capped here.</summary>
    public const int SmsMaxLength = 1_600;

    /// <summary>Push payloads are small by platform mandate, not by convention.</summary>
    public const int PushMaxLength = 4_000;

    /// <summary>A practical ceiling rather than a protocol one — email has no useful limit.</summary>
    public const int EmailMaxLength = 500_000;

    /// <summary>Webhook payloads are JSON documents; this bounds the request size.</summary>
    public const int WebhookMaxLength = 256_000;

    /// <summary>Longest accepted subject line.</summary>
    public const int SubjectMaxLength = 500;

    private MessageBody(string content, string? subject)
    {
        Content = content;
        Subject = subject;
    }

    /// <summary>The body text.</summary>
    public string Content { get; }

    /// <summary>The subject line. Non-null only on channels that have one.</summary>
    public string? Subject { get; }

    /// <summary>Creates a body, rejecting one the channel cannot carry.</summary>
    public static Result<MessageBody> Create(ChannelType channel, string? content, string? subject = null)
    {
        if (channel is ChannelType.None)
        {
            return MessageErrors.ChannelMissing;
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return MessageErrors.BodyEmpty;
        }

        int limit = MaxLengthFor(channel);
        if (content.Length > limit)
        {
            return MessageErrors.BodyTooLong(channel, limit, content.Length);
        }

        Result<string?> normalisedSubject = NormaliseSubject(channel, subject);

        return normalisedSubject.IsFailure
            ? Result<MessageBody>.Failure(normalisedSubject.Error)
            : new MessageBody(content, normalisedSubject.Value);
    }

    private static Result<string?> NormaliseSubject(ChannelType channel, string? subject)
    {
        bool supportsSubject = channel is ChannelType.Email or ChannelType.Push;

        if (string.IsNullOrWhiteSpace(subject))
        {
            return Result<string?>.Success(null);
        }

        if (!supportsSubject)
        {
            return Result<string?>.Failure(MessageErrors.SubjectNotValidForChannel(channel));
        }

        string trimmed = subject.Trim();

        return trimmed.Length > SubjectMaxLength
            ? Result<string?>.Failure(
                MessageErrors.BodyTooLong(channel, SubjectMaxLength, trimmed.Length))
            : Result<string?>.Success(trimmed);
    }

    private static int MaxLengthFor(ChannelType channel) => channel switch
    {
        ChannelType.Email => EmailMaxLength,
        ChannelType.Sms => SmsMaxLength,
        ChannelType.Push => PushMaxLength,
        ChannelType.Webhook => WebhookMaxLength,
        _ => 0,
    };

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Content;
        yield return Subject;
    }
}
