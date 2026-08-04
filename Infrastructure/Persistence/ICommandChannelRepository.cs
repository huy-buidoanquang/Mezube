namespace Mezube.Infrastructure.Persistence;

public interface ICommandChannelRepository
{
    Task<IReadOnlyList<long>> ListAsync(long clanId, CancellationToken cancellationToken = default);

    Task<bool> IsAllowedAsync(long clanId, long channelId, CancellationToken cancellationToken = default);

    Task AddAsync(long clanId, long channelId, long? addedBy, CancellationToken cancellationToken = default);

    Task RemoveAsync(long clanId, long channelId, CancellationToken cancellationToken = default);

    Task ClearAsync(long clanId, CancellationToken cancellationToken = default);
}
