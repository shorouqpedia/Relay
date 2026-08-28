using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Relay.Domain.Messaging;
using Relay.Infrastructure.Callbacks;
using Relay.Infrastructure.Persistence;
using Relay.Providers.Abstractions;

namespace Relay.IntegrationTests.Api;

/// <summary>
/// The callback endpoint, end to end.
/// </summary>
/// <remarks>
/// The only public write path in the system, so these are as much security tests
/// as functional ones: a forged signature, a replayed callback, and a body
/// altered after signing all have to be refused, and refused without saying why.
/// <para>Scenario ids <c>CB01</c>–<c>CB12</c> in <c>docs/test-plan.md</c>.</para>
/// </remarks>
[Collection(RelayApiCollection.Name)]
public sealed class CallbackEndpointTests(RelayApiFactory factory)
{
    private const string CallbackSecret = RelayApiFactory.PostalCallbackSecret;
    private const string Endpoint = "/api/v1/callbacks/email.postal";

    [Fact]
    public async Task CB01_Callback_WithAValidSignature_IsAcknowledged()
    {
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await PostSignedAsync(
            client, Body("message.delivered", "unknown-to-relay"));

        // 204 even though no message matched. The status answers "did you receive
        // this?", and a provider reading anything else resends.
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task CB02_Callback_MarksASentMessageDelivered()
    {
        HttpClient client = factory.CreateClient();
        string providerMessageId = await SeedSentMessageAsync();

        HttpResponseMessage response = await PostSignedAsync(
            client, Body("message.delivered", providerMessageId));

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using RelayDbContext context = factory.NewDbContext();
        string status = await StatusOfAsync(context, providerMessageId);

        status.ShouldBe("Delivered");
    }

    [Fact]
    public async Task CB03_Callback_MarksASentMessageFailedWithItsReason()
    {
        HttpClient client = factory.CreateClient();
        string providerMessageId = await SeedSentMessageAsync();

        await PostSignedAsync(client, Body("message.bounced", providerMessageId, "mailbox full"));

        await using RelayDbContext context = factory.NewDbContext();

        Message? message = await FindAsync(context, providerMessageId);
        message.ShouldNotBeNull();
        message.Status.ToString().ShouldBe("Failed");
        message.FailureReason.ShouldBe("mailbox full");
    }

    [Fact]
    public async Task CB04_TheSameCallbackTwice_IsAcknowledgedBothTimes()
    {
        HttpClient client = factory.CreateClient();
        string providerMessageId = await SeedSentMessageAsync();

        HttpResponseMessage first = await PostSignedAsync(
            client, Body("message.delivered", providerMessageId));
        HttpResponseMessage second = await PostSignedAsync(
            client, Body("message.delivered", providerMessageId));

        // The most common case once a provider starts retrying, and the one a
        // conflict response would make permanent: a 4xx tells the provider the
        // receipt did not land, so it sends it again, forever.
        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        second.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task CB05_Callback_WithAForgedSignature_Is401AndChangesNothing()
    {
        HttpClient client = factory.CreateClient();
        string providerMessageId = await SeedSentMessageAsync();
        string body = Body("message.delivered", providerMessageId);

        HttpResponseMessage response = await PostAsync(
            client,
            body,
            timestamp: UnixNow(),
            signature: "sha256=" + new string('a', 64));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        await using RelayDbContext context = factory.NewDbContext();
        (await StatusOfAsync(context, providerMessageId)).ShouldBe("Sent");
    }

    [Fact]
    public async Task CB06_Callback_SignedWithTheWrongSecret_Is401()
    {
        HttpClient client = factory.CreateClient();
        string body = Body("message.delivered", "anything");
        string timestamp = UnixNow();

        HttpResponseMessage response = await PostAsync(
            client,
            body,
            timestamp,
            "sha256=" + CallbackSignature.ComputeHex(
                "a-completely-different-secret-thats-long-enough", $"{timestamp}.{body}"));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CB07_Callback_WithABodyAlteredAfterSigning_Is401()
    {
        HttpClient client = factory.CreateClient();
        string victim = await SeedSentMessageAsync();

        string signedBody = Body("message.delivered", "some-other-message");
        string timestamp = UnixNow();
        string signature = "sha256=" + CallbackSignature.ComputeHex(
            CallbackSecret, $"{timestamp}.{signedBody}");

        // The attack the signature exists for: take a genuine callback and point
        // it at a different message, keeping the signature.
        string tamperedBody = Body("message.delivered", victim);

        HttpResponseMessage response = await PostAsync(client, tamperedBody, timestamp, signature);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        await using RelayDbContext context = factory.NewDbContext();
        (await StatusOfAsync(context, victim)).ShouldBe("Sent");
    }

    [Fact]
    public async Task CB08_Callback_ReplayedWithAnOldTimestamp_Is401()
    {
        HttpClient client = factory.CreateClient();
        string providerMessageId = await SeedSentMessageAsync();
        string body = Body("message.delivered", providerMessageId);

        // A correctly signed callback from two hours ago. Without the timestamp
        // inside the signed string, this is indistinguishable from a fresh one and
        // stays valid forever.
        string timestamp = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds()
            .ToString(CultureInfo.InvariantCulture);

        HttpResponseMessage response = await PostAsync(
            client,
            body,
            timestamp,
            "sha256=" + CallbackSignature.ComputeHex(CallbackSecret, $"{timestamp}.{body}"));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        await using RelayDbContext context = factory.NewDbContext();
        (await StatusOfAsync(context, providerMessageId)).ShouldBe("Sent");
    }

    [Fact]
    public async Task CB09_Callback_WithNoSignatureHeaders_Is401()
    {
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.PostAsync(
            Endpoint,
            new StringContent(Body("message.delivered", "x"), Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CB10_Callback_SignedButUnreadable_Is422NotUnauthorized()
    {
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await PostSignedAsync(client, "{ this is not json");

        // Distinguished from a forgery deliberately. A correctly signed payload
        // that cannot be read means the provider changed its format — a bug to
        // fix, and one that a blanket 401 would hide among the noise of internet
        // background traffic.
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task CB11_Callback_ForAnUnregisteredProvider_Is404()
    {
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.PostAsync(
            "/api/v1/callbacks/sms.nonexistent",
            new StringContent("{}", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CB12_EveryCallback_IsRecorded_IncludingTheRejectedOnes()
    {
        HttpClient client = factory.CreateClient();
        string providerMessageId = await SeedSentMessageAsync();

        await PostSignedAsync(client, Body("message.delivered", providerMessageId));
        await PostAsync(client, Body("message.delivered", providerMessageId), UnixNow(), "sha256=bad");
        await client.PostAsync(
            "/api/v1/callbacks/sms.nonexistent",
            new StringContent("{}", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        await using RelayDbContext context = factory.NewDbContext();

        List<CallbackRecord> records = await context.CallbackRecords
            .AsNoTracking()
            .OrderByDescending(r => r.ReceivedAt)
            .Take(20)
            .ToListAsync(TestContext.Current.CancellationToken);

        // The rejected ones are the point. A log containing only successes cannot
        // distinguish "the provider never told us" from "the provider told us and
        // we threw it away", which are very different conversations to have with
        // a vendor.
        records.Select(r => r.Disposition).ShouldContain(CallbackDisposition.Applied);
        records.Select(r => r.Disposition).ShouldContain(CallbackDisposition.Rejected);
        records.Select(r => r.Disposition).ShouldContain(CallbackDisposition.UnknownProvider);

        CallbackRecord rejected = records.First(r => r.Disposition == CallbackDisposition.Rejected);

        // Kept for Relay's operators, never returned to the caller.
        rejected.Detail.ShouldNotBeNullOrWhiteSpace();
    }

    private static string UnixNow() =>
        DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    private static string Body(string @event, string messageId, string? detail = null) =>
        JsonSerializer.Serialize(new
        {
            @event,
            message_id = messageId,
            detail,
            occurred_at = DateTimeOffset.UtcNow,
        });

    private static Task<HttpResponseMessage> PostSignedAsync(HttpClient client, string body)
    {
        string timestamp = UnixNow();

        return PostAsync(
            client,
            body,
            timestamp,
            "sha256=" + CallbackSignature.ComputeHex(CallbackSecret, $"{timestamp}.{body}"));
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string body,
        string timestamp,
        string signature)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        request.Headers.Add("X-Postal-Timestamp", timestamp);
        request.Headers.Add("X-Postal-Signature", signature);

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Submits a message and walks it to <c>Sent</c> with a known provider id.
    /// </summary>
    /// <remarks>
    /// Done through the domain rather than by writing rows, so the message is in a
    /// state the system could actually have produced. A hand-built row can encode
    /// a combination the aggregate would never allow, and a test built on one
    /// proves nothing about the real path.
    /// </remarks>
    private async Task<string> SeedSentMessageAsync()
    {
        string providerMessageId = $"postal-{Guid.CreateVersion7():N}";

        await using RelayDbContext context = factory.NewDbContext();

        Message message = Message.Submit(
            IdempotencyKey.Create($"cb-{Guid.CreateVersion7():N}").Value,
            Recipient.Create(ChannelType.Email, "callback@example.com").Value,
            MessageBody.Create(ChannelType.Email, "Body.", "Subject").Value,
            maxAttempts: 3,
            DateTimeOffset.UtcNow).Value;

        message.BeginDispatch(ProviderId.Create("email.postal").Value, DateTimeOffset.UtcNow);
        message.RecordAttempt(
            AttemptOutcome.Accepted, providerMessageId, null, TimeSpan.Zero, DateTimeOffset.UtcNow);

        context.Messages.Add(message);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return providerMessageId;
    }

    private static async Task<Message?> FindAsync(RelayDbContext context, string providerMessageId) =>
        await context.Messages
            .AsNoTracking()
            .Include(m => m.Attempts)
            .FirstOrDefaultAsync(
                m => m.Attempts.Any(a => a.ProviderMessageId == providerMessageId),
                TestContext.Current.CancellationToken);

    private static async Task<string> StatusOfAsync(RelayDbContext context, string providerMessageId)
    {
        Message? message = await FindAsync(context, providerMessageId);
        return message?.Status.ToString() ?? "not found";
    }
}
