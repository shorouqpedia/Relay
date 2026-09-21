using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Relay.IntegrationTests.Api;

/// <summary>
/// The HTTP surface, end to end.
/// </summary>
/// <remarks>
/// Real requests, the real host, a real database. Nothing is substituted, so
/// these exercise the parts that only exist once the pieces are assembled: the
/// validation filter actually running, the error mapping producing the status a
/// client will branch on, and idempotency surviving an identical second request.
/// <para>Scenario ids <c>IT01</c>–<c>IT12</c> in <c>docs/test-plan.md</c>.</para>
/// </remarks>
[Collection(RelayApiCollection.Name)]
public sealed class MessageEndpointTests(RelayApiFactory factory) : IAsyncLifetime
{
    public ValueTask InitializeAsync() => new(factory.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task IT01_Submit_WithAValidRequest_Returns201AndTheMessageId()
    {
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/messages",
            Request(Key()),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        SubmitResponse? body = await response.Content
            .ReadFromJsonAsync<SubmitResponse>(TestContext.Current.CancellationToken);

        body.ShouldNotBeNull();
        body.Id.ShouldNotBe(Guid.Empty);
        body.Status.ShouldBe("Pending");
        response.Headers.Location!.ToString().ShouldContain(body.Id.ToString());
    }

    [Fact]
    public async Task IT02_Submit_Twice_WithTheSameKey_Returns200AndTheSameMessage()
    {
        HttpClient client = factory.CreateClient();
        string key = Key();

        HttpResponseMessage first = await client.PostAsJsonAsync(
            "/api/v1/messages", Request(key), TestContext.Current.CancellationToken);
        HttpResponseMessage second = await client.PostAsJsonAsync(
            "/api/v1/messages", Request(key), TestContext.Current.CancellationToken);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);

        // 200, not 409. A caller retrying after a timeout wants the outcome, and
        // an error would make them handle a failure for something that worked.
        second.StatusCode.ShouldBe(HttpStatusCode.OK);

        SubmitResponse a = (await first.Content
            .ReadFromJsonAsync<SubmitResponse>(TestContext.Current.CancellationToken))!;
        SubmitResponse b = (await second.Content
            .ReadFromJsonAsync<SubmitResponse>(TestContext.Current.CancellationToken))!;

        b.Id.ShouldBe(a.Id);
    }

    [Fact]
    public async Task IT03_Submit_WithTheIdempotencyKeyHeader_DeduplicatesToo()
    {
        HttpClient client = factory.CreateClient();
        string key = Key();

        HttpResponseMessage first = await PostWithHeaderAsync(client, key);
        HttpResponseMessage second = await PostWithHeaderAsync(client, key);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task IT04_Submit_WithNoKeyAtAll_DeduplicatesIdenticalRequests()
    {
        HttpClient client = factory.CreateClient();
        object request = new
        {
            channel = "Email",
            recipient = $"derived-{Guid.CreateVersion7():N}@example.com",
            body = "Identical body.",
            subject = "Identical subject",
        };

        HttpResponseMessage first = await client.PostAsJsonAsync(
            "/api/v1/messages", request, TestContext.Current.CancellationToken);
        HttpResponseMessage second = await client.PostAsJsonAsync(
            "/api/v1/messages", request, TestContext.Current.CancellationToken);

        // The derived key. Weaker than a caller-supplied one — two genuinely
        // separate messages with identical content collapse — but it means a
        // client that does nothing still cannot double-send by retrying.
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task IT05_Submit_WithAnInvalidRecipientForTheChannel_Returns400()
    {
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/messages",
            new
            {
                channel = "Sms",
                recipient = "not-a-phone-number",
                body = "Body.",
                idempotencyKey = Key(),
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        JsonElement problem = await response.Content
            .ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        // The stable code, not the prose. A client branches on this; the detail
        // text is free to change.
        problem.GetProperty("code").GetString()
            .ShouldBe("message.recipient_invalid_for_channel");
    }

    [Fact]
    public async Task IT06_Submit_WithAnEmptyBody_IsRejectedByTheValidationFilter()
    {
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/messages",
            new { channel = "Email", recipient = "a@example.com", body = "", idempotencyKey = Key() },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        JsonElement problem = await response.Content
            .ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        // The filter's shape, with per-field errors — proving it ran, rather than
        // the handler reporting the first problem it happened to hit.
        problem.GetProperty("errors").GetProperty("Body").GetArrayLength()
            .ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task IT07_Submit_WithASubjectOnSms_Returns400()
    {
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/messages",
            new
            {
                channel = "Sms",
                recipient = "+201001234567",
                body = "Body.",
                subject = "SMS has no subject",
                idempotencyKey = Key(),
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task IT08_Get_ReturnsTheMessageAndItsEmptyHistory()
    {
        HttpClient client = factory.CreateClient();

        SubmitResponse submitted = await SubmitAsync(client, Key());

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/messages/{submitted.Id}", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonElement view = await response.Content
            .ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        view.GetProperty("status").GetString().ShouldBe("Pending");
        view.GetProperty("channel").GetString().ShouldBe("Email");
        view.GetProperty("attempts").GetArrayLength().ShouldBe(0);
        view.GetProperty("maxAttempts").GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task IT09_Get_ForAMessageThatDoesNotExist_Returns404WithAProblem()
    {
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/messages/{Guid.CreateVersion7()}", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        JsonElement problem = await response.Content
            .ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        problem.GetProperty("code").GetString().ShouldBe("message.not_found");
    }

    [Fact]
    public async Task IT10_Cancel_WhilePending_Returns204AndTheMessageIsCancelled()
    {
        HttpClient client = factory.CreateClient();
        SubmitResponse submitted = await SubmitAsync(client, Key());

        HttpResponseMessage cancelled = await client.PostAsync(
            $"/api/v1/messages/{submitted.Id}/cancel",
            content: null,
            TestContext.Current.CancellationToken);

        cancelled.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        JsonElement view = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/messages/{submitted.Id}", TestContext.Current.CancellationToken);

        view.GetProperty("status").GetString().ShouldBe("Cancelled");
    }

    [Fact]
    public async Task IT11_Cancel_Twice_IsStillSuccessful()
    {
        HttpClient client = factory.CreateClient();
        SubmitResponse submitted = await SubmitAsync(client, Key());

        await client.PostAsync(
            $"/api/v1/messages/{submitted.Id}/cancel", null, TestContext.Current.CancellationToken);

        HttpResponseMessage second = await client.PostAsync(
            $"/api/v1/messages/{submitted.Id}/cancel", null, TestContext.Current.CancellationToken);

        // The caller asked for a state and the message is in it. Reporting a
        // conflict would make a successful retry look like a failure.
        second.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task IT12_Cancel_AMessageThatDoesNotExist_Returns404()
    {
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.PostAsync(
            $"/api/v1/messages/{Guid.CreateVersion7()}/cancel",
            content: null,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static string Key() => $"it-{Guid.CreateVersion7():N}";

    private static object Request(string key) => new
    {
        channel = "Email",
        recipient = "recipient@example.com",
        body = "Body text.",
        subject = "Subject",
        idempotencyKey = key,
    };

    private static async Task<SubmitResponse> SubmitAsync(HttpClient client, string key)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/messages", Request(key), TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();

        return (await response.Content
            .ReadFromJsonAsync<SubmitResponse>(TestContext.Current.CancellationToken))!;
    }

    private static async Task<HttpResponseMessage> PostWithHeaderAsync(HttpClient client, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/messages")
        {
            Content = JsonContent.Create(new
            {
                channel = "Email",
                recipient = "header@example.com",
                body = "Body text.",
                subject = "Subject",
            }),
        };

        request.Headers.Add("Idempotency-Key", key);

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private sealed record SubmitResponse(Guid Id, string Status);
}
