using Relay.Domain.Common;
using Relay.Domain.Messaging;

namespace Relay.Domain.UnitTests.Messaging;

/// <summary>
/// The rules the value objects carry, which is the reason they exist rather than
/// being strings on the aggregate.
/// </summary>
/// <remarks>Scenario ids <c>VO01</c>–<c>VO16</c> in <c>docs/test-plan.md</c>.</remarks>
public sealed class ValueObjectTests
{
    [Theory]
    [InlineData(ChannelType.Email, "someone@example.com")]
    [InlineData(ChannelType.Email, "first.last+tag@sub.example.co.uk")]
    [InlineData(ChannelType.Sms, "+201001234567")]
    [InlineData(ChannelType.Sms, "+15551234567")]
    [InlineData(ChannelType.Push, "d3f1a9c04b7e2856aa10")]
    [InlineData(ChannelType.Webhook, "https://hooks.example.com/inbound")]
    public void VO01_Recipient_AcceptsAddressesValidForTheirChannel(ChannelType channel, string address)
    {
        Recipient.Create(channel, address).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(ChannelType.Email, "not-an-address")]
    [InlineData(ChannelType.Email, "missing@tld")]
    [InlineData(ChannelType.Email, "two@@example.com")]
    [InlineData(ChannelType.Sms, "01001234567")]
    [InlineData(ChannelType.Sms, "+0123456789")]
    [InlineData(ChannelType.Push, "too-short")]
    [InlineData(ChannelType.Webhook, "http://hooks.example.com/inbound")]
    [InlineData(ChannelType.Webhook, "hooks.example.com")]
    public void VO02_Recipient_RejectsAddressesInvalidForTheirChannel(ChannelType channel, string address)
    {
        Result<Recipient> result = Recipient.Create(channel, address);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("message.recipient_invalid_for_channel");
    }

    [Fact]
    public void VO03_Recipient_RejectsAnSmsAddressOnTheEmailChannel()
    {
        // The point of binding channel and address together: this pairing is
        // individually well-formed on both sides and still wrong.
        Recipient.Create(ChannelType.Email, "+201001234567").IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void VO04_Recipient_LowercasesEmailSoTwoSpellingsAreOneAddress()
    {
        Recipient upper = Recipient.Create(ChannelType.Email, "Someone@Example.COM").Value;
        Recipient lower = Recipient.Create(ChannelType.Email, "someone@example.com").Value;

        upper.Address.ShouldBe("someone@example.com");
        upper.ShouldBe(lower);
    }

    [Fact]
    public void VO05_Recipient_RejectsAMissingAddress()
    {
        Recipient.Create(ChannelType.Email, "   ").Error.Code.ShouldBe("message.recipient_missing");
    }

    [Fact]
    public void VO06_Recipient_RejectsTheUnsetChannel()
    {
        Recipient.Create(ChannelType.None, "a@example.com").Error.Code.ShouldBe("message.channel_missing");
    }

    [Fact]
    public void VO07_IdempotencyKey_NormalisesCaseAndSurroundingWhitespace()
    {
        IdempotencyKey padded = IdempotencyKey.Create("  Order-12345  ").Value;
        IdempotencyKey plain = IdempotencyKey.Create("order-12345").Value;

        // A caller retrying a request does not reliably reproduce the original
        // spacing. If these compared unequal, the retry would send a second message.
        padded.Value.ShouldBe("order-12345");
        padded.ShouldBe(plain);
        padded.GetHashCode().ShouldBe(plain.GetHashCode());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short")]
    public void VO08_IdempotencyKey_RejectsKeysThatCannotServeTheirPurpose(string? value)
    {
        IdempotencyKey.Create(value).Error.Code.ShouldBe("message.idempotency_key_invalid");
    }

    [Fact]
    public void VO09_IdempotencyKey_RejectsAKeyLongerThanTheUniqueIndex()
    {
        string tooLong = new('a', IdempotencyKey.MaxLength + 1);

        IdempotencyKey.Create(tooLong).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void VO10_MessageBody_RejectsEmptyContent()
    {
        MessageBody.Create(ChannelType.Sms, "  ").Error.Code.ShouldBe("message.body_empty");
    }

    [Fact]
    public void VO11_MessageBody_EnforcesThePerChannelLengthLimit()
    {
        string tooLongForSms = new('x', MessageBody.SmsMaxLength + 1);

        Result<MessageBody> sms = MessageBody.Create(ChannelType.Sms, tooLongForSms);
        Result<MessageBody> email = MessageBody.Create(ChannelType.Email, tooLongForSms);

        // The same body, accepted on one channel and refused on another. That is
        // the rule being a property of the channel rather than of the request.
        sms.IsFailure.ShouldBeTrue();
        sms.Error.Code.ShouldBe("message.body_too_long");
        email.IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(ChannelType.Email)]
    [InlineData(ChannelType.Push)]
    public void VO12_MessageBody_AcceptsASubjectOnChannelsThatHaveOne(ChannelType channel)
    {
        MessageBody body = MessageBody.Create(channel, "Body", "  A subject  ").Value;

        body.Subject.ShouldBe("A subject");
    }

    [Theory]
    [InlineData(ChannelType.Sms)]
    [InlineData(ChannelType.Webhook)]
    public void VO13_MessageBody_RejectsASubjectOnChannelsThatHaveNone(ChannelType channel)
    {
        Result<MessageBody> result = MessageBody.Create(channel, "Body", "A subject");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("message.subject_invalid_for_channel");
    }

    [Fact]
    public void VO14_MessageBody_TreatsABlankSubjectAsAbsentOnEveryChannel()
    {
        MessageBody.Create(ChannelType.Sms, "Body", "   ").IsSuccess.ShouldBeTrue();
        MessageBody.Create(ChannelType.Email, "Body", "   ").Value.Subject.ShouldBeNull();
    }

    [Theory]
    [InlineData("email.postal")]
    [InlineData("sms.twinkle")]
    [InlineData("push-beacon")]
    [InlineData("webhook")]
    public void VO15_ProviderId_AcceptsSlugs(string value)
    {
        ProviderId.Create(value).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Email.Postal")]
    [InlineData("email_postal")]
    [InlineData(".email")]
    [InlineData("email..postal")]
    [InlineData("email postal")]
    public void VO16_ProviderId_RejectsAnythingThatIsNotOne(string value)
    {
        // "Email.Postal" is rejected rather than normalised: a provider id appears
        // in configuration and in routing rules, and silently accepting two
        // spellings of one id makes a typo in a routing rule look like it worked.
        ProviderId.Create(value).IsFailure.ShouldBeTrue();
    }
}
