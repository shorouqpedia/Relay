using Microsoft.Extensions.Logging;
using Relay.Infrastructure.Persistence.Outbox;

namespace Relay.Worker;

/// <summary>
/// Publishes domain events to the log.
/// </summary>
/// <remarks>
/// A stand-in until a broker is wired up, and honest about being one. It keeps
/// the outbox loop exercised end to end — rows are claimed, published, and
/// marked — so the mechanism is known to work before there is anything on the
/// other end of it.
/// <para>
/// It is not a fake in the misleading sense: at-least-once delivery to a log is
/// still at-least-once delivery, and swapping it for a broker client changes one
/// registration line and nothing else.
/// </para>
/// </remarks>
internal sealed class LoggingOutboxPublisher(ILogger<LoggingOutboxPublisher> logger) : IOutboxPublisher
{
    public Task PublishAsync(string type, string payload, CancellationToken cancellationToken)
    {
        logger.LogInformation("Domain event {EventType}: {Payload}", type, payload);
        return Task.CompletedTask;
    }
}
