using Mezon.Net.Client;
using Mezon.Net.Sdk.Builders;
using Mezube.Bot;
using Mezube.Domain.Entities;
using Mezube.Music;
using Mezube.Music.Interactive;
using Mezube.Playback;
using Mezube.Stn;
using System.Buffers;
using System.Net;
using System.Text.Json;

namespace Mezube.Ui;

public static class PlayerMessageBuilder
{
    private const string ColorPlaying = "#e11d48";
    private const string ColorInfo = "#64748b";
    private const string ColorOk = "#16a34a";
    private const string ColorError = "#dc2626";

    private static BotOptions _options = new();
    private static readonly Lazy<JsonElement> EqualizerPoolInputs = new(BuildEqualizerPoolInputs, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly ThreadLocal<ArrayBufferWriter<byte>> EmbedBuffer =
        new(() => new ArrayBufferWriter<byte>(1024));

    public static void Configure(BotOptions options)
        => _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Map STN HTTP conflict/capacity (and known bodies) to a clear user-facing embed.</summary>
    public static MessageContent? FromStnFailure(Exception ex)
    {
        if (ex is StnVoiceException stn)
        {
            var body = stn.Body;
            if (stn.IsUnavailable || StnServerLoad.MentionsCapacity(body))
            {
                return Error(
                    "Voice servers busy",
                    "We’re out of free voice slots for a moment. Try again shortly.");
            }

            if (stn.IsCapacityExceeded)
            {
                return Error(
                    "Voice servers full",
                    "No free voice rooms right now. Give it a moment and try again.");
            }

            if (stn.IsConflict || stn.StatusCode == HttpStatusCode.Conflict)
            {
                if (Contains(body, "whip room already active")
                    || Contains(body, "whip voice publisher active")
                    || Contains(body, "voice v2 publisher active")
                    || Contains(body, "voice v2 room already active"))
                {
                    return Error(
                        "Room already in use",
                        "Something else is already streaming in that voice channel. Use !stop, then try again.");
                }

                return Error(
                    "Room already in use",
                    "Another stream already owns that voice channel. Stop it first, then try again.");
            }

            if (Contains(body, "download") || Contains(body, "publisher failed") || Contains(body, "failed"))
            {
                return Error(
                    "Couldn’t start playback",
                    "I couldn’t fetch or stream that track. Try another link, or try again shortly.");
            }

            return null;
        }

        // Streaming WS failures and WaitForPublishingAsync surface plain InvalidOperationException.
        var message = ex.Message ?? string.Empty;
        if (Contains(message, "503") || StnServerLoad.MentionsCapacity(message))
        {
            return Error(
                "Voice servers busy",
                "We’re out of free voice slots for a moment. Try again shortly.");
        }

        if (Contains(message, "download_failed") || Contains(message, "404"))
        {
            return Error(
                "Audio missing",
                "That file isn’t on the CDN anymore. Queue it again so I can re-upload.");
        }

        if (Contains(message, "publish failed") || Contains(message, "publisher"))
        {
            return Error(
                "Couldn’t start playback",
                "I couldn’t stream that track. Try another link, or try again shortly.");
        }

        return null;

        static bool Contains(string haystack, string needle)
            => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>yt-dlp / CDN / local media prep failures (not STN room teardown).</summary>
    public static MessageContent? FromMediaFailure(Exception ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            var msg = cur.Message ?? string.Empty;
            if (Contains(msg, "403") || Contains(msg, "Forbidden"))
            {
                return Error(
                    "Couldn’t fetch that track",
                    [
                        new("Track", "Source blocked the download (often temporary)."),
                        new("Next", "I’ll skip ahead if there’s more in the queue — try another link later."),
                    ]);
            }

            if (Contains(msg, "yt-dlp") || Contains(msg, "download returned no file") || Contains(msg, "CDN upload failed"))
            {
                return Error(
                    "Couldn’t prepare that track",
                    [
                        new("What happened", "Download or upload failed for this song."),
                        new("Next", "Skipping it so the room stays open — try another track."),
                    ]);
            }

            if (Contains(msg, "requires .ogg") || Contains(msg, ".opus"))
            {
                return Error(
                    "Unsupported audio format",
                    "That file isn’t playable as Opus — try a different link.");
            }
        }

        return null;

        static bool Contains(string haystack, string needle)
            => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    public static MessageContent NowPlaying(
        TrackInfoEntity track,
        int queueCount,
        string destination,
        string? nextTitle = null,
        long? controlMessageId = null,
        long? controlUserId = null,
        long? clanId = null,
        bool includeMusicViz = false)
    {
        var fields = new List<MessageEmbedField>
        {
            new("Channel", Escape(destination), inline: true),
            new("Duration", track.DisplayDuration, inline: true),
            new("Source", FriendlySource(track.Source), inline: true),
            new("In queue", queueCount.ToString(), inline: true),
            new("Next up", Escape(TruncateTitle(nextTitle)), inline: true),
        };

        if (!string.IsNullOrWhiteSpace(track.RequestedBy))
        {
            fields.Add(new("Requested by", Escape(track.RequestedBy), inline: true));
        }

        if (includeMusicViz)
        {
            var vizField = TryCreateMusicVizField();
            if (vizField is not null)
            {
                fields.Add(vizField);
            }
        }

        string? skipId = null;
        string? stopId = null;
        if (controlMessageId is long messageId && controlUserId is long userId)
        {
            skipId = MezubeButtonId.CreatePlayerControl(messageId, userId, MezubeButtonId.ActionSkip, clanId);
            stopId = MezubeButtonId.CreatePlayerControl(messageId, userId, MezubeButtonId.ActionStop, clanId);
        }

        return Build(
            "Now playing",
            Escape(track.Title),
            ColorPlaying,
            track.ThumbnailUrl,
            track.WebpageUrl,
            fields: fields,
            skipButtonId: skipId,
            stopButtonId: stopId);
    }

    private const int NextTitleMaxChars = 64;

    private static string TruncateTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "—";
        }

        var trimmed = title.Trim();
        if (trimmed.Length <= NextTitleMaxChars)
        {
            return trimmed;
        }

        return trimmed[..(NextTitleMaxChars - 3)] + "...";
    }

    public static MessageContent Queued(TrackInfoEntity track, int position, string? channelLabel = null)
    {
        var fields = new List<MessageEmbedField>
        {
            new("Position", $"#{position}", inline: true),
            new("Duration", track.DisplayDuration, inline: true),
            new("Source", FriendlySource(track.Source), inline: true),
        };
        if (!string.IsNullOrWhiteSpace(channelLabel))
        {
            fields.Add(new("Channel", Escape(FormatChannelMention(channelLabel)), inline: true));
        }

        return Build(
            "Added to queue",
            Escape(track.Title),
            ColorOk,
            track.ThumbnailUrl,
            track.WebpageUrl,
            includeControls: false,
            fields);
    }

    public static MessageContent UpNext(TrackInfoEntity track, int secondsRemaining = 10, string? channelLabel = null)
    {
        var fields = new List<MessageEmbedField>
        {
            new("Starts in", $"{secondsRemaining}s", inline: true),
            new("Duration", track.DisplayDuration, inline: true),
            new("Source", FriendlySource(track.Source), inline: true),
        };
        if (!string.IsNullOrWhiteSpace(channelLabel))
        {
            fields.Add(new("Channel", Escape(FormatChannelMention(channelLabel)), inline: true));
        }

        if (!string.IsNullOrWhiteSpace(track.RequestedBy))
        {
            fields.Add(new("Requested by", Escape(track.RequestedBy), inline: true));
        }

        return Build(
            "Up next",
            Escape(track.Title),
            ColorInfo,
            track.ThumbnailUrl,
            track.WebpageUrl,
            includeControls: false,
            fields);
    }

    public static MessageContent QueueList(QueuedPlay? current, IReadOnlyList<QueuedPlay> upcoming)
    {
        if (current is null && upcoming.Count == 0)
        {
            return Status("Queue", [new("Empty", "Nothing waiting — add a track with !play.")]);
        }

        var fields = new List<MessageEmbedField>
        {
            new(
                "Waiting",
                current is null
                    ? $"{upcoming.Count} track(s)"
                    : $"{upcoming.Count} track(s) after now playing"),
        };
        if (current is not null)
        {
            fields.Add(new("Now", Escape(current.Track.Title), inline: true));
            fields.Add(new("Duration", current.Track.DisplayDuration, inline: true));
            fields.Add(new("Channel", Escape(FormatChannelLabel(current.Target)), inline: true));
        }

        for (var i = 0; i < upcoming.Count; i++)
        {
            var item = upcoming[i];
            fields.Add(new($"#{i + 1}", Escape(item.Track.Title), inline: true));
            fields.Add(new("Duration", item.Track.DisplayDuration, inline: true));
            fields.Add(new("Channel", Escape(FormatChannelLabel(item.Target)), inline: true));
        }

        return Build(
            "Queue",
            string.Empty,
            ColorInfo,
            current?.Track.ThumbnailUrl,
            current?.Track.WebpageUrl,
            includeControls: false,
            fields);
    }

    public static MessageContent Preparing(string destination)
    {
        var fields = new List<MessageEmbedField>
        {
            new("Destination", Escape(destination), inline: true),
        };

        return Build(
            "Hold on a second, I’m floating among the clouds, searching for your song.",
            string.Empty,
            ColorInfo,
            thumbnailUrl: null,
            url: null,
            includeControls: false,
            fields);
    }

    public static string FormatDestination(string mode, string? channelLabel)
        => string.IsNullOrWhiteSpace(channelLabel)
            ? mode
            : $"{mode} · {FormatChannelMention(channelLabel)}";

    /// <summary>Display form <c>#channel_label</c> (strips a leading # if already present).</summary>
    public static string FormatChannelMention(string? channelLabel, long? channelId = null)
    {
        if (!string.IsNullOrWhiteSpace(channelLabel))
        {
            var label = channelLabel.Trim().TrimStart('#');
            return string.IsNullOrEmpty(label) ? (channelId is long id ? $"#{id}" : "#unknown") : $"#{label}";
        }

        return channelId is long fallback ? $"#{fallback}" : "#unknown";
    }

    private static string FormatChannelLabel(PlaybackTarget target)
        => FormatChannelMention(target.ChannelLabel, target.ChannelId);

    public static MessageContent NotAllowed(string detail)
        => Error("Hold up", detail);

    public static MessageContent CopyrightBlocked()
        => Build(
            Mezube.Domain.MezubeConstants.TitleCopyrightBlocked,
            string.Empty,
            ColorError,
            thumbnailUrl: null,
            url: null,
            includeControls: false,
            fields:
            [
                new("Why", "This track is too large or blocked, so I can’t play it right now."),
            ]);

    public static MessageContent QueueFull()
        => Build(
            Mezube.Domain.MezubeConstants.TitleQueueFull,
            string.Empty,
            ColorInfo,
            thumbnailUrl: null,
            url: null,
            includeControls: false,
            fields:
            [
                new("Tip", "Skip or stop a few tracks, then try again."),
            ]);

    public static MessageContent PlaybackSlotsFull()
        => Build(
            Mezube.Domain.MezubeConstants.TitlePlaybackSlotsFull,
            string.Empty,
            ColorInfo,
            thumbnailUrl: null,
            url: null,
            includeControls: false,
            fields:
            [
                new("Tip", "Wait for another clan to finish, then queue again."),
            ]);

    public static MessageContent Status(string title, string detail)
        => Status(title, [new("Info", detail)]);

    public static MessageContent Status(string title, IReadOnlyList<MessageEmbedField> fields)
        => Build(title, string.Empty, ColorInfo, thumbnailUrl: null, url: null, includeControls: false, fields);

    public static MessageContent Ok(string title, string detail)
        => Ok(title, [new("Done", detail)]);

    public static MessageContent Ok(string title, IReadOnlyList<MessageEmbedField> fields)
        => Build(title, string.Empty, ColorOk, thumbnailUrl: null, url: null, includeControls: false, fields);

    public static MessageContent Error(string title, string detail)
        => Error(title, [new("What happened", detail)]);

    public static MessageContent Error(string title, IReadOnlyList<MessageEmbedField> fields)
        => Build(title, string.Empty, ColorError, thumbnailUrl: null, url: null, includeControls: false, fields);

    /// <summary>Generic reply for unexpected exceptions (no internal details).</summary>
    public static MessageContent Awkward()
        => Error(
            "Well, this is awkward...",
            "Something took an unexpected coffee break. Try that again in a moment.");

    public static MessageContent RateLimited()
        => Status(
            "Taking a quick breather!",
            [new("Please wait", "You’re typing faster than we can think — give it about 30 seconds.")]);

    public static MessageContent NothingPlaying()
        => Status("Nothing playing", [new("Queue", "Empty — drop something in with !play.")]);

    public static MessageContent TrackNotFound(string? hint = null)
        => Error(
            "No match",
            [
                new(
                    "Try",
                    string.IsNullOrWhiteSpace(hint)
                        ? "Another search, a direct link, or !play with no args for examples."
                        : hint),
            ]);

    public static MessageContent PlaylistCatalog(IReadOnlyList<(string Name, bool IsDefault)> playlists, string prefix)
    {
        if (playlists.Count == 0)
        {
            return Status(
                "Playlists",
                [
                    new("Empty", "No playlists yet — create one with !playlist create <name>."),
                ]);
        }

        var fields = playlists
            .Select(p => new MessageEmbedField(
                p.IsDefault ? $"{p.Name} · default" : p.Name,
                p.IsDefault ? "Plays when the queue goes idle." : "Saved for this clan.",
                inline: true))
            .ToList();
        fields.Add(new("Tip", $"{prefix}playlist list <name> shows the tracks inside."));
        return Status("Playlists", fields);
    }

    public static MessageContent PlaylistTracks(
        string playlistName,
        bool isDefault,
        IReadOnlyList<(string Title, string Duration, bool TooLarge)> tracks,
        int totalCount,
        int previewCap = 25)
    {
        if (tracks.Count == 0)
        {
            return Status(
                $"Playlist · {Escape(playlistName)}",
                [
                    new("Empty", "Add tracks with !playlist add, or import with !playlist create <name> <url>."),
                ]);
        }

        var fields = new List<MessageEmbedField>
        {
            new(
                "About",
                isDefault
                    ? $"{Escape(playlistName)} · default · {totalCount} track(s)"
                    : $"{Escape(playlistName)} · {totalCount} track(s)"),
        };

        for (var i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            var suffix = t.TooLarge ? " · skipped (too large)" : string.Empty;
            fields.Add(new($"#{i + 1}", $"{Escape(t.Title)} ({t.Duration}){suffix}"));
        }

        if (totalCount > previewCap)
        {
            fields.Add(new("More", $"…and {totalCount - previewCap} more not shown."));
        }

        return Status("Playlist", fields);
    }

    public static MessageContent ClanSettings(
        string djRole,
        string loop,
        string defaultPlaylist,
        string playChannels)
        => Status(
            "Clan settings",
            [
                new("DJ role", djRole),
                new("Loop", loop),
                new("Default playlist", defaultPlaylist),
                new("Play channels", playChannels),
            ]);

    public static MessageContent MusicChannelsListed(string body)
        => Status(
            "Music channels",
            [
                new(
                    "Allowlist",
                    string.IsNullOrWhiteSpace(body)
                        ? "Every channel can queue music."
                        : body),
            ]);

    public static MessageContent PlayingNext(string trackTitle, string reason)
        => Ok(
            "Playing next",
            [
                new("Track", Escape(trackTitle)),
                new("Why", reason),
            ]);

    public static MessageContent SoundCloudSetQueued(int added, int cap, bool interruptedDefault)
        => Ok(
            interruptedDefault ? "Playing next" : "SoundCloud set queued",
            [
                new("Added", $"{added} track(s)"),
                new("Queue cap", cap.ToString()),
                new(
                    "Note",
                    interruptedDefault
                        ? "Cut in ahead of the default playlist."
                        : "Tracks will play in order."),
            ]);

    public static MessageContent PlaylistImportDone(string playlistName, int added)
        => Ok(
            "Playlist updated",
            [
                new("Playlist", Escape(playlistName)),
                new("Added", $"{added} track(s)"),
                new("Next", "I’m preparing audio in the background — you’ll get a quiet ping when it’s ready."),
            ]);

    public static MessageContent ModeConflict(bool wantVoice)
        => Error(
            "Already playing in another mode",
            wantVoice
                ? "This clan is on a stream right now. Use !play #voice … or !stop before switching."
                : "This clan is in a voice channel right now. Use !play #stream … or !stop before switching.");

    public static MessageContent PlaylistPrepDone(string playlistName, int ready, int total, int failed)
    {
        var fields = new List<MessageEmbedField>
        {
            new("Playlist", Escape(playlistName)),
            new("Ready", $"{ready}/{total}"),
        };
        if (failed > 0)
        {
            fields.Add(new("Skipped", $"{failed} couldn’t prepare."));
        }

        return Ok("Playlist ready", fields);
    }

    public static MessageContent PickerNotYours(string detail)
        => NotAllowed(detail);

    public static string FriendlySource(string source)
        => source switch
        {
            "youtube" => "YouTube",
            "soundcloud" => "SoundCloud",
            "url" => "Direct link",
            _ => string.IsNullOrWhiteSpace(source) ? "Unknown" : source,
        };

    /// <param name="isDjOrOwner">DJ role member or clan owner.</param>
    /// <param name="isOwner">Clan owner only.</param>
    public static MessageContent Help(string prefix, bool isDjOrOwner = false, bool isOwner = false)
    {
        var about = isOwner
            ? "Here’s everything you can run as clan owner — including DJ and channel setup."
            : isDjOrOwner
                ? "Here’s what you can do with DJ powers — play, control playback, and manage the default playlist."
                : "Here’s what you can do — play tracks, check the queue, and manage your own requests.";
        var fields = new List<MessageEmbedField> { new("About", about) };
        fields.AddRange(HelpFields(prefix, isDjOrOwner, isOwner)
            .Select(f => new MessageEmbedField(f.Name, f.Value)));
        return Build(
            "Mezube help",
            string.Empty,
            ColorInfo,
            thumbnailUrl: null,
            url: null,
            includeControls: false,
            fields);
    }

    /// <summary>Role-filtered help fields (testable without Mezon message parse).</summary>
    public static IReadOnlyList<(string Name, string Value)> HelpFields(
        string prefix,
        bool isDjOrOwner = false,
        bool isOwner = false)
    {
        var p = prefix;
        var fields = new List<(string Name, string Value)>
        {
            (
                "Play",
                $"""
                · {p}play <query>
                · {p}play #channel <query>
                Alias: {p}p — free-text search may show a picker (up to 5 results)
                """),
            (
                "Controls",
                isDjOrOwner
                    ? $"""
                      · {p}skip · {p}pause · {p}resume · {p}stop
                      · {p}voteskip · {p}queue · {p}np · {p}loop · {p}seek
                      """
                    : $"""
                      · {p}voteskip · {p}queue · {p}np · {p}loop · {p}seek
                      · {p}skip · {p}pause · {p}resume — your track only
                      """),
            (
                "Playlist",
                isDjOrOwner
                    ? $"""
                      · {p}playlist create | add | play | list | delete
                      · {p}playlist play [#channel] <name>
                      · {p}playlist list <name>
                      · {p}playlist default <name|none>
                      """
                    : $"""
                      · {p}playlist create | add | play | list | delete
                      · {p}playlist play [#channel] <name>
                      · {p}playlist list <name>
                      """),
            (
                "Info",
                $"· {p}settings"),
        };

        if (isOwner)
        {
            fields.Add((
                "Admin",
                $"""
                · {p}setdj @role | none
                · {p}musicchannel add | remove | list | clear
                """));
        }

        return fields;
    }

    /// <summary>Shown when <c>!play</c> is invoked with no query.</summary>
    public static MessageContent PlayUsage(string prefix)
    {
        var fields = PlayUsageFields(prefix)
            .Select(f => new MessageEmbedField(f.Name, f.Value))
            .ToList();

        return Build(
            "Missing track",
            string.Empty,
            ColorError,
            thumbnailUrl: null,
            url: null,
            includeControls: false,
            fields);
    }

    public static IReadOnlyList<(string Name, string Value)> PlayUsageFields(string prefix)
    {
        var p = prefix;
        return
        [
            (
                "YouTube",
                $"""
                · {p}play never gonna give you up
                · {p}play https://youtu.be/dQw4w9WgXcQ
                · {p}play https://www.youtube.com/watch?v=dQw4w9WgXcQ
                · {p}play https://music.youtube.com/watch?v=…
                """),
            (
                "SoundCloud",
                $"""
                · {p}play https://soundcloud.com/artist/track
                · {p}play https://soundcloud.com/artist/sets/playlist-name
                · {p}play https://on.soundcloud.com/…
                """),
            (
                "Channel",
                $"· {p}play #voice-or-stream <query>"),
        ];
    }

    public static MessageContent Text(string title, string description)
        => Status(title, description);

    public static MessageContent SearchPick(
        string query,
        IReadOnlyList<TrackCandidate> candidates,
        long messageId,
        long userId)
    {
        var options = BuildRadioOptions(candidates);
        var fields = new List<MessageEmbedField>
        {
            CreateRadioEmbedField(
                "Results",
                $"Looking for “{Escape(query)}” — pick one, then tap Play selected.",
                MezubeButtonId.RadioSearch,
                options,
                // Omit max_options: presence enables multi-select in Mezon client.
                maxOptions: null),
        };
        var submitId = MezubeButtonId.CreateSearchPick(messageId, userId, MezubeButtonId.ActionSubmit);
        var components = new ButtonBuilder()
            .AddButton(submitId, "Play selected", style: (int)MessageButtonStyle.Success)
            .Build();
        return Build(
            "Pick a track",
            string.Empty,
            ColorInfo,
            thumbnailUrl: candidates.FirstOrDefault()?.ThumbnailUrl,
            url: null,
            fields,
            componentsJson: components);
    }

    public static MessageContent PlaylistImportPick(PlaylistImportSession session, long messageId, long userId)
    {
        var pageItems = session.PageCandidates();
        var options = BuildRadioOptions(pageItems);
        var remaining = Math.Max(0, PlaylistImportSession.SelectMax - session.SelectedTokens.Count);
        var maxOptions = Math.Max(1, Math.Min(remaining, Math.Max(1, pageItems.Count)));
        var selectedPreview = session.SelectedTokens.Count == 0
            ? "none"
            : string.Join(", ", session.SelectedTokens.Take(10).Select(t =>
            {
                var c = session.Candidates.FirstOrDefault(x => x.Token == t);
                return c is null ? t : Escape(c.Title);
            }));

        var fields = new List<MessageEmbedField>
        {
            CreateRadioEmbedField(
                $"Page {session.Page + 1}/{session.PageCount}",
                "Select tracks on this page (they stack up). Confirm adds up to 10.",
                MezubeButtonId.RadioPlaylist,
                options,
                maxOptions: maxOptions),
            new("Selected", $"{session.SelectedTokens.Count}/{PlaylistImportSession.SelectMax}\n{selectedPreview}"),
        };

        var builder = new ButtonBuilder();
        if (session.Page > 0)
        {
            builder.AddButton(
                MezubeButtonId.CreatePlaylistImport(messageId, userId, MezubeButtonId.ActionPrev),
                "Prev",
                style: (int)MessageButtonStyle.Secondary);
        }

        if (session.Page + 1 < session.PageCount)
        {
            builder.AddButton(
                MezubeButtonId.CreatePlaylistImport(messageId, userId, MezubeButtonId.ActionNext),
                "Next",
                style: (int)MessageButtonStyle.Secondary);
        }

        builder.AddButton(
            MezubeButtonId.CreatePlaylistImport(messageId, userId, MezubeButtonId.ActionConfirm),
            "Confirm",
            style: (int)MessageButtonStyle.Success);
        builder.AddButton(
            MezubeButtonId.CreatePlaylistImport(messageId, userId, MezubeButtonId.ActionCancel),
            "Cancel",
            style: (int)MessageButtonStyle.Danger);

        return Build(
            $"Import · {Escape(session.PlaylistName)}",
            string.Empty,
            ColorInfo,
            thumbnailUrl: pageItems.FirstOrDefault()?.ThumbnailUrl,
            url: null,
            fields,
            componentsJson: builder.Build());
    }

    /// <summary>
    /// Mezon client renders RADIO via embed field <c>inputs</c> (mezon-js InteractiveBuilder.addRadioField),
    /// not via top-level message <c>components</c> (those are for buttons / action rows).
    /// </summary>
    private static MessageEmbedField CreateRadioEmbedField(
        string name,
        string description,
        string radioId,
        IReadOnlyList<MessageRadioOption> options,
        int? maxOptions)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("id", radioId);
            writer.WriteNumber("type", (int)MessageComponentType.Radio);
            if (maxOptions is int max)
            {
                writer.WriteNumber("max_options", max);
            }

            writer.WriteStartArray("component");
            foreach (var opt in options)
            {
                writer.WriteStartObject();
                writer.WriteString("label", opt.Label);
                writer.WriteString("value", opt.Value);
                writer.WriteString("name", opt.Name);
                if (!string.IsNullOrWhiteSpace(opt.Description))
                {
                    writer.WriteString("description", opt.Description);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(stream.ToArray());
        return new MessageEmbedField(name, description, inline: false, inputs: doc.RootElement.Clone());
    }

    private static List<MessageRadioOption> BuildRadioOptions(IReadOnlyList<TrackCandidate> candidates)
    {
        var options = new List<MessageRadioOption>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            var label = Truncate($"{i + 1}. {c.Title}", 80);
            options.Add(new MessageRadioOption(
                Label: label,
                Value: c.Token,
                Name: label,
                Description: Truncate($"{c.DisplayDuration} · {FriendlySource(c.Source)}", 100)));
        }

        return options;
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..(max - 1)] + "…";

    private static MessageContent Build(
        string title,
        string description,
        string color,
        string? thumbnailUrl,
        string? url,
        bool includeControls,
        IReadOnlyList<MessageEmbedField>? fields = null)
        => Build(title, description, color, thumbnailUrl, url, fields, skipButtonId: includeControls ? "mezube_skip" : null, stopButtonId: includeControls ? "mezube_stop" : null);

    private static MessageContent Build(
        string title,
        string description,
        string color,
        string? thumbnailUrl,
        string? url,
        IReadOnlyList<MessageEmbedField>? fields = null,
        string? skipButtonId = null,
        string? stopButtonId = null,
        string? componentsJson = null)
    {
        var avatarUrl = _options.BotAvatarUrl;
        var displayName = string.IsNullOrWhiteSpace(_options.BotDisplayName) ? "Mezube" : _options.BotDisplayName;
        var resolvedThumbnail = string.IsNullOrWhiteSpace(thumbnailUrl) ? avatarUrl : thumbnailUrl;

        var buffer = EmbedBuffer.Value!;
        buffer.Clear();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("embed");
            writer.WriteStartObject();
            writer.WriteString("color", color);
            writer.WriteStartObject("author");
            writer.WriteString("name", displayName);
            if (!string.IsNullOrWhiteSpace(avatarUrl))
            {
                writer.WriteString("icon_url", avatarUrl);
            }

            writer.WriteEndObject();
            writer.WriteString("title", title);
            writer.WriteString("description", description);
            if (!string.IsNullOrWhiteSpace(url))
            {
                writer.WriteString("url", url);
            }

            if (!string.IsNullOrWhiteSpace(resolvedThumbnail))
            {
                writer.WriteStartObject("thumbnail");
                writer.WriteString("url", resolvedThumbnail);
                writer.WriteEndObject();
            }

            if (fields is { Count: > 0 })
            {
                writer.WriteStartArray("fields");
                foreach (var field in fields)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", field.Name);
                    writer.WriteString("value", field.Value);
                    if (field.Inline)
                    {
                        writer.WriteBoolean("inline", true);
                    }

                    if (field.Inputs is JsonElement inputs)
                    {
                        writer.WritePropertyName("inputs");
                        inputs.WriteTo(writer);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteStartObject("footer");
            writer.WriteString("text", displayName);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndArray();

            if (!string.IsNullOrWhiteSpace(componentsJson))
            {
                writer.WritePropertyName("components");
                writer.WriteRawValue($"[{{\"components\":{componentsJson}}}]");
            }
            else if (!string.IsNullOrWhiteSpace(skipButtonId) && !string.IsNullOrWhiteSpace(stopButtonId))
            {
                // Mezon UI expects action rows: [{ "components": [ button, … ] }] (pm-assistant / mezon-sdk).
                var buttonsJson = new ButtonBuilder()
                    .AddButton(skipButtonId, "Skip", style: (int)MessageButtonStyle.Primary)
                    .AddButton(stopButtonId, "Stop", style: (int)MessageButtonStyle.Danger)
                    .Build();
                writer.WritePropertyName("components");
                writer.WriteRawValue($"[{{\"components\":{buttonsJson}}}]");
            }

            writer.WriteEndObject();
        }

        var json = System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
        return MessageContent.Parse(json);
    }

    private static string Escape(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static MessageEmbedField? TryCreateMusicVizField()
    {
        var imageUrl = _options.VizImageUrl;
        var positionUrl = _options.VizPositionUrl;
        if (string.IsNullOrWhiteSpace(imageUrl) || string.IsNullOrWhiteSpace(positionUrl))
        {
            return null;
        }

        // Clone cached pool + inject current CDN URLs (URLs may be filled after viz warm-up).
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("id", "mezube_viz");
            writer.WriteNumber("type", 6); // EMessageComponentType.ANIMATION
            writer.WriteStartObject("component");
            writer.WriteString("url_image", imageUrl);
            writer.WriteString("url_position", positionUrl);
            writer.WritePropertyName("pool");
            EqualizerPoolInputs.Value.WriteTo(writer);
            writer.WriteNumber("duration", 1.6);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(stream.ToArray());
        return new MessageEmbedField(string.Empty, string.Empty, inline: false, inputs: doc.RootElement.Clone());
    }

    /// <summary>Cached equalizer pool frames (UTF-8 JSON array) — built once.</summary>
    private static JsonElement BuildEqualizerPoolInputs()
    {
        var pool = BuildEqualizerPool();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var column in pool)
            {
                writer.WriteStartArray();
                foreach (var frame in column)
                {
                    writer.WriteStringValue(frame);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndArray();
        }

        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Dense multi-cell EQ pool. Frame keys match Assets/viz atlas:
    /// <c>{col}{height}</c> e.g. <c>05</c>, <c>010</c> (col 0..9, height 1..10).
    /// </summary>
    private static string[][] BuildEqualizerPool()
    {
        const int columns = 10;
        const int steps = 24;
        const int maxHeight = 10;
        var pool = new string[columns][];
        for (var c = 0; c < columns; c++)
        {
            var seq = new string[steps];
            for (var s = 0; s < steps; s++)
            {
                var t = s / (double)steps * Math.Tau;
                var phase = c * 0.7;
                var wave =
                    0.42 * Math.Sin(t * 2.2 + phase)
                    + 0.30 * Math.Sin(t * 3.8 + phase * 1.5)
                    + 0.18 * Math.Sin(t * 5.5 + phase * 0.7)
                    + 0.10 * Math.Sin(t * 7.5 + phase * 2.0)
                    + 0.08 * Math.Sin(t * 2.8 + (c + 0.5) * 0.7);
                wave = Math.Clamp(wave, -1.0, 1.0);
                var level = 0.05 + 0.95 * ((wave + 1) * 0.5);
                var height = Math.Clamp((int)Math.Round(level * maxHeight), 1, maxHeight);
                seq[s] = $"{c}{height}";
            }

            pool[c] = seq;
        }

        return pool;
    }
}
