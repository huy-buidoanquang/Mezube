using Mezon.Net.Client;
using Mezon.Net.Sdk.Commands;
using Mezube.Domain.Entities;
using Mezube.Helpers;
using Mezube.Infrastructure.Persistence.Redis;
using Mezube.Music.Interactive;
using Mezube.Ui;
using Microsoft.Extensions.Logging;

namespace Mezube.Music;

public sealed partial class MusicPlayer
{
    public async Task SetLoopAsync(ICommandContext ctx, CancellationToken cancellationToken = default)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        if (!await _access.CanStopAsync(ctx.Client, clanId, ctx.Author.Id, cancellationToken).ConfigureAwait(false))
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.NotAllowed(
                    "Only a DJ or the clan owner can change loop mode."))
                .ConfigureAwait(false);
            return;
        }

        var arg = ctx.Args.Count > 0 ? ctx.Args[0].Trim().ToLowerInvariant() : "cycle";
        var current = await _playerStore.GetLoopModeAsync(clanId, cancellationToken).ConfigureAwait(false);
        LoopMode next = arg switch
        {
            "off" or "none" or "0" => LoopMode.Off,
            "track" or "song" or "one" or "1" => LoopMode.Track,
            "queue" or "all" or "2" => LoopMode.Queue,
            _ => current switch
            {
                LoopMode.Off => LoopMode.Track,
                LoopMode.Track => LoopMode.Queue,
                _ => LoopMode.Off,
            },
        };
        await _playerStore.SetLoopModeAsync(clanId, next, cancellationToken).ConfigureAwait(false);
        await ctx.ReplyAsync(PlayerMessageBuilder.Ok(
                "Loop mode",
                next switch
                {
                    LoopMode.Track => "Looping the current track.",
                    LoopMode.Queue => "Looping the whole queue.",
                    _ => "Loop disabled.",
                }))
            .ConfigureAwait(false);
    }

    public async Task SeekAsync(ICommandContext ctx, CancellationToken cancellationToken = default)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        if (!TryGetState(clanId, out var state)
            || !state.IsPlaying
            || state.Queue.CurrentItem is null
            || state.Target is null)
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.NothingPlaying())
                .ConfigureAwait(false);
            return;
        }

        if (!await _access.CanSkipAsync(
                ctx.Client,
                clanId,
                ctx.Author.Id,
                state.Queue.CurrentItem.Track.RequestedByUserId,
                cancellationToken).ConfigureAwait(false))
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.NotAllowed(
                    "Only the person who queued this track, a DJ, or the clan owner can seek."))
                .ConfigureAwait(false);
            return;
        }

        if (state.Mode != PlaybackMode.Voice || !_options.StnWhipEnabled)
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                    "Seek unavailable",
                    "Jumping in a track only works for voice (WHIP) playback right now."))
                .ConfigureAwait(false);
            return;
        }

        if (ctx.Args.Count == 0)
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                    "Missing position",
                    [
                        new("Examples", $"{_options.CommandPrefix}seek 1:30"),
                        new("Relative", $"{_options.CommandPrefix}seek +15  or  {_options.CommandPrefix}seek -10"),
                    ]))
                .ConfigureAwait(false);
            return;
        }

        var durationMs = (long)(state.Queue.Current!.Duration?.TotalMilliseconds ?? 0);
        if (durationMs <= 0)
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                    "Couldn’t seek",
                    "I don’t know how long this track is."))
                .ConfigureAwait(false);
            return;
        }

        var currentPos = await _playerStore.EffectivePositionMsAsync(clanId, cancellationToken).ConfigureAwait(false);
        if (!TryParseSeek(ctx.Args[0], currentPos, durationMs, out var targetMs))
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Error(
                    "Hmm, that time doesn’t look right",
                    "Try mm:ss, plain seconds, or relative +N / -N."))
                .ConfigureAwait(false);
            return;
        }

        await _playerStore.SetPositionAsync(clanId, targetMs, durationMs, paused: false, cancellationToken)
            .ConfigureAwait(false);

        state.PendingSeekMs = targetMs;
        state.ReplayCurrent = true;
        state.LastDestroyReason = PlayerDestroyReason.Seek;
        state.CancelTrack();

        await ctx.ReplyAsync(PlayerMessageBuilder.Ok(
                "Jumped ahead",
                $"Now at {TimeSpan.FromMilliseconds(targetMs):m\\:ss}."))
            .ConfigureAwait(false);
    }

    public async Task VoteSkipAsync(ICommandContext ctx, CancellationToken cancellationToken = default)
    {
        var clanId = ctx.Clan?.Id ?? ctx.Channel.ClanId;
        if (!TryGetState(clanId, out var state))
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Status(
                    "Nothing playing",
                    "There’s no track to vote on right now."))
                .ConfigureAwait(false);
            return;
        }

        var historyId = state.PlayHistoryId
            ?? await _playerStore.GetPlayHistoryIdAsync(clanId, cancellationToken).ConfigureAwait(false);
        if (historyId is not long hid)
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Status(
                    "Nothing playing",
                    "There’s no track to vote on right now."))
                .ConfigureAwait(false);
            return;
        }

        var (met, votes, needed, error) = await _access.TryVoteSkipAsync(
                clanId,
                hid,
                ctx.Author.Id,
                state.Target?.ChannelId,
                cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Error("Vote-skip", error)).ConfigureAwait(false);
            return;
        }

        if (!met)
        {
            await ctx.ReplyAsync(PlayerMessageBuilder.Ok(
                    "Vote recorded",
                    [
                        new("Progress", $"{votes}/{needed}"),
                    ]))
                .ConfigureAwait(false);
            return;
        }

        state.LastDestroyReason = PlayerDestroyReason.Skip;
        await SkipStateAsync(state, cancellationToken).ConfigureAwait(false);
        await ctx.ReplyAsync(PlayerMessageBuilder.Ok(
                "Skipping!",
                [
                    new("Votes", $"{votes}/{needed}"),
                    new("Next", "Moving on to the next track."),
                ]))
            .ConfigureAwait(false);
    }


    private static bool TryParseSeek(string raw, long currentMs, long durationMs, out long targetMs)
    {
        targetMs = 0;
        var text = raw.Trim();
        if (text.StartsWith('+') || text.StartsWith('-'))
        {
            if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var deltaSec))
            {
                return false;
            }

            targetMs = (long)Math.Clamp(currentMs + deltaSec * 1000, 0, durationMs);
            return true;
        }

        if (text.Contains(':', StringComparison.Ordinal))
        {
            var parts = text.Split(':');
            if (parts.Length is < 2 or > 3)
            {
                return false;
            }

            if (!int.TryParse(parts[^1], out var seconds)
                || !int.TryParse(parts[^2], out var minutes))
            {
                return false;
            }

            var hours = 0;
            if (parts.Length == 3 && !int.TryParse(parts[0], out hours))
            {
                return false;
            }

            targetMs = (long)Math.Clamp(((hours * 3600) + (minutes * 60) + seconds) * 1000L, 0, durationMs);
            return true;
        }

        if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var sec))
        {
            return false;
        }

        targetMs = (long)Math.Clamp(sec * 1000, 0, durationMs);
        return true;
    }
}
