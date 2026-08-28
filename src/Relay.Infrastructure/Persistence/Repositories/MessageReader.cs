using Microsoft.EntityFrameworkCore;
using Relay.Application.Messages;
using Relay.Domain.Messaging;

namespace Relay.Infrastructure.Persistence.Repositories;

/// <summary>
/// Projects messages straight to their read model.
/// </summary>
/// <remarks>
/// No <c>Include</c>, no tracking, and no <see cref="Message"/> is ever
/// constructed. The projection is translated into the <c>SELECT</c>, so the
/// database returns the columns the caller will see and nothing else — the body
/// of a 500KB email is not fetched to build a status response that does not
/// contain it.
/// <para>
/// This is what the read half of the CQRS split buys, and it is only available
/// because reads go through their own interface. A shared repository returning
/// aggregates could not do it.
/// </para>
/// </remarks>
internal sealed class MessageReader(RelayDbContext context) : IMessageReader
{
    public async Task<MessageView?> FindAsync(MessageId id, CancellationToken cancellationToken) =>
        await context.Messages
            .AsNoTracking()
            .Where(m => m.Id == id)
            .Select(m => new MessageView(
                m.Id.Value,
                m.Recipient.Channel.ToString(),
                m.Recipient.Address,
                m.Body.Subject,
                m.Status.ToString(),
                m.Attempts.Count,
                m.MaxAttempts,
                m.FailureReason,
                m.CreatedAt,
                m.SentAt,
                m.CompletedAt,
                m.Attempts
                    .OrderBy(a => a.Sequence)
                    .Select(a => new AttemptView(
                        a.Sequence,
                        a.ProviderId.Value,
                        a.Outcome.ToString(),
                        a.FailureReason,
                        a.Duration.TotalMilliseconds,
                        a.AttemptedAt))
                    .ToList()))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
}
