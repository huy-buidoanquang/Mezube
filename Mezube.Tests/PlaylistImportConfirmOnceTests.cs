using System.Collections.Concurrent;
using Mezon.Net.Client;
using Mezon.Net.Core;
using Mezon.Net.Models;
using Mezon.Net.Sdk;
using Mezon.Net.Sdk.Entities;
using Mezon.Net.Sdk.Interactions;
using Mezube.Bot;
using Mezube.Domain.Entities;
using Mezube.Infrastructure.Persistence;
using Mezube.Infrastructure.Persistence.Redis;
using Mezube.Music;
using Mezube.Music.Interactive;
using Mezube.Ui;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using MezonClient = Mezon.Net.Sdk.MezonClient;

namespace Mezube.Tests;

public sealed class PlaylistImportConfirmOnceTests
{
    private const long MessageId = 100;
    private const long UserId = 7;

    [Fact]
    public async Task HandlePlaylistImportButtonAsync_concurrent_confirms_add_each_track_once()
    {
        await using var client = new MezonClient(new MezonClientOptions());
        var user = await client.GetUserAsync(UserId);
        var sessions = new FakeSessionStore();
        sessions.Imports[MessageId] = NewSession("youtube:a", "youtube:b");
        var playlists = new GatedPlaylistRepository();
        var player = NewPlayer(sessions, playlists);
        var parts = new MezubeButtonId.Parsed(MezubeButtonId.PlaylistImport, MessageId, UserId, MezubeButtonId.ActionConfirm, null);

        var first = player.HandlePlaylistImportButtonAsync(new FakeInteractionContext(user), parts);
        var second = player.HandlePlaylistImportButtonAsync(new FakeInteractionContext(user), parts);
        playlists.Gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(new[] { 1L, 2L }, playlists.AddedTrackIds.Order());
        Assert.Empty(sessions.Imports);
    }

    [Fact]
    public async Task HandlePlaylistImportButtonAsync_confirm_with_nothing_selected_keeps_session()
    {
        await using var client = new MezonClient(new MezonClientOptions());
        var user = await client.GetUserAsync(UserId);
        var sessions = new FakeSessionStore();
        sessions.Imports[MessageId] = NewSession();
        var playlists = new GatedPlaylistRepository();
        playlists.Gate.SetResult();
        var player = NewPlayer(sessions, playlists);
        var parts = new MezubeButtonId.Parsed(MezubeButtonId.PlaylistImport, MessageId, UserId, MezubeButtonId.ActionConfirm, null);
        var ctx = new FakeInteractionContext(user);

        await player.HandlePlaylistImportButtonAsync(ctx, parts);

        Assert.Empty(playlists.AddedTrackIds);
        Assert.True(sessions.Imports.ContainsKey(MessageId));
        Assert.Single(ctx.Updates);
    }

    private static PlaylistImportSession NewSession(params string[] selected) => new()
    {
        UserId = UserId,
        PlaylistId = 5,
        PlaylistName = "mix",
        Candidates =
        [
            new TrackCandidate { Source = "youtube", ExternalId = "a", Title = "A", WebpageUrl = "https://youtu.be/a", TrackId = 1 },
            new TrackCandidate { Source = "youtube", ExternalId = "b", Title = "B", WebpageUrl = "https://youtu.be/b", TrackId = 2 },
        ],
        SelectedTokens = [.. selected],
    };

    private static MusicPlayer NewPlayer(IInteractiveSessionStore sessions, IPlaylistRepository playlists)
        => new(
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            new BotOptions(),
            null!,
            null!,
            null!,
            playlists,
            null!,
            null!,
            null!,
            null!,
            sessions,
            null!,
            new ApplicationLifetime(NullLogger<ApplicationLifetime>.Instance),
            NullLogger<MusicPlayer>.Instance);

    private sealed class FakeSessionStore : IInteractiveSessionStore
    {
        public ConcurrentDictionary<long, PlaylistImportSession> Imports { get; } = new();

        public Task SaveSearchPickAsync(long messageId, SearchPickSession session, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SearchPickSession?> TryGetSearchPickAsync(long messageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SearchPickSession?> TakeSearchPickAsync(long messageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteSearchPickAsync(long messageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SavePlaylistImportAsync(long messageId, PlaylistImportSession session, CancellationToken cancellationToken = default)
        {
            Imports[messageId] = session;
            return Task.CompletedTask;
        }

        public Task<PlaylistImportSession?> TryGetPlaylistImportAsync(long messageId, CancellationToken cancellationToken = default)
            => Task.FromResult(Imports.TryGetValue(messageId, out var session) ? session : null);

        public Task<PlaylistImportSession?> TakePlaylistImportAsync(long messageId, CancellationToken cancellationToken = default)
            => Task.FromResult(Imports.TryRemove(messageId, out var session) ? session : null);

        public Task DeletePlaylistImportAsync(long messageId, CancellationToken cancellationToken = default)
        {
            Imports.TryRemove(messageId, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class GatedPlaylistRepository : IPlaylistRepository
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<long> AddedTrackIds { get; } = [];

        public async Task AddItemAsync(long playlistId, long trackId, long? addedBy, CancellationToken cancellationToken = default)
        {
            lock (AddedTrackIds)
            {
                AddedTrackIds.Add(trackId);
            }

            await Gate.Task;
        }

        public Task<PlaylistEntity?> TryGetByNameAsync(long clanId, string name, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PlaylistEntity?> TryGetDefaultAsync(long clanId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<long>> ListDefaultClanIdsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<PlaylistEntity>> ListAsync(long clanId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PlaylistEntity> CreateAsync(long clanId, string name, long? createdBy, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> DeleteAsync(long clanId, string name, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SetDefaultAsync(long clanId, long? playlistId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<PlaylistItemEntity>> ListItemsAsync(long playlistId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeInteractionContext(User user) : IInteractionContext
    {
        public List<MessageContent> Updates { get; } = [];

        public MezonClient Client => null!;
        public IInteraction Interaction { get; } = new ButtonInteraction(MessageId, 1, "button", UserId, UserId);
        public Channel Channel => null!;
        public Message? Message => null;
        public User User => user;
        public CancellationToken CancellationToken => CancellationToken.None;

        public Task<ChannelMessageAckResponse> RespondAsync(MessageContent content, RequestOptions? options = null)
            => Task.FromResult(default(ChannelMessageAckResponse));

        public Task<ChannelMessageAckResponse> RespondTextAsync(string text, RequestOptions? options = null)
            => Task.FromResult(default(ChannelMessageAckResponse));

        public Task UpdateMessageAsync(MessageContent content, RequestOptions? options = null)
        {
            Updates.Add(content);
            return Task.CompletedTask;
        }

        public Task UpdateMessageTextAsync(string text, RequestOptions? options = null)
            => Task.CompletedTask;

        public Task<ChannelMessageAckResponse> SendEphemeralAsync(MessageContent content, RequestOptions? options = null)
            => Task.FromResult(default(ChannelMessageAckResponse));

        public Task<ChannelMessageAckResponse> SendEphemeralTextAsync(string text, RequestOptions? options = null)
            => Task.FromResult(default(ChannelMessageAckResponse));

        public Task<ChannelMessageAckResponse> UpdateEphemeralAsync(long messageId, MessageContent content, RequestOptions? options = null)
            => Task.FromResult(default(ChannelMessageAckResponse));

        public Task<ChannelMessageAckResponse> UpdateEphemeralTextAsync(long messageId, string text, RequestOptions? options = null)
            => Task.FromResult(default(ChannelMessageAckResponse));

        public Task<ChannelMessageAckResponse> DeleteEphemeralAsync(long messageId, RequestOptions? options = null)
            => Task.FromResult(default(ChannelMessageAckResponse));
    }
}
