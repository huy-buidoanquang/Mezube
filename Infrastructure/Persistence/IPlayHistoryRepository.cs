using Mezube.Domain.Entities;

namespace Mezube.Infrastructure.Persistence;

public interface IPlayHistoryRepository
{
    Task<long> StartAsync(
        long clanId,
        long trackId,
        string mode,
        long channelId,
        long? requestedByUserId,
        CancellationToken cancellationToken = default);

    Task<bool> CloseAsync(long historyId, string endReason, CancellationToken cancellationToken = default);

    Task CloseOpenForClanAsync(long clanId, string endReason, CancellationToken cancellationToken = default);
}
