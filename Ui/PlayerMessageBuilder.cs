using Mezon.Net.Client;
using Mezon.Net.Sdk.Builders;
using Mezube.Bot;
using Mezube.Domain.Entities;
using Mezube.Music;
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
            if (stn.IsCapacityExceeded
                || Contains(body, "max concurrent voice rooms reached")
                || Contains(body, "max concurrent whip"))
            {
                return Error(
                    "Voice capacity full",
                    "STN has no free voice rooms right now. Try again in a moment.");
            }

            if (stn.IsConflict || stn.StatusCode == HttpStatusCode.Conflict)
            {
                if (Contains(body, "whip room already active")
                    || Contains(body, "whip voice publisher active"))
                {
                    return Error(
                        "Room busy",
                        "This voice room already has a WHIP publisher. Stop it first, then try again.");
                }

                if (Contains(body, "voice v2 publisher active")
                    || Contains(body, "voice v2 room already active"))
                {
                    return Error(
                        "Room busy",
                        "This voice room already has an active v2 job. Use !stop, then try again.");
                }

                if (Contains(body, "legacy voice publisher active"))
                {
                    return Error(
                        "Room busy",
                        "This voice room is used by a legacy publisher. Stop it first, then try again.");
                }

                return Error(
                    "Room conflict",
                    "Another publisher already owns this voice room. Stop it first, then try again.");
            }

            if (Contains(body, "download") || Contains(body, "publisher failed") || Contains(body, "failed"))
            {
                return Error(
                    "Playback failed",
                    "STN could not fetch or publish that track. Try another URL or try again shortly.");
            }

            return null;
        }

        // WaitForPublishingAsync throws InvalidOperationException with STN status codes.
        var message = ex.Message ?? string.Empty;
        if (Contains(message, "download_failed") || Contains(message, "404"))
        {
            return Error(
                "Media unavailable",
                "The audio file was not reachable on CDN. Re-queue the track to re-upload.");
        }

        if (Contains(message, "publish failed") || Contains(message, "publisher"))
        {
            return Error(
                "Playback failed",
                "STN could not publish that track. Try another URL or try again shortly.");
        }

        return null;

        static bool Contains(string haystack, string needle)
            => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    public static MessageContent NowPlaying(
        TrackInfoEntity track,
        int queueCount,
        string destination,
        TimeSpan? position = null,
        long? controlMessageId = null,
        long? controlUserId = null,
        long? clanId = null,
        bool includeMusicViz = false)
    {
        var fields = new List<MessageEmbedField>
        {
            new("Destination", Escape(destination), inline: true),
            new("Duration", track.DisplayDuration, inline: true),
            new("Source", track.Source, inline: true),
            new("Queued next", queueCount.ToString(), inline: true),
        };

        if (position is { } p)
        {
            // Clamp to known duration (if present) to avoid displaying values past the end.
            if (track.Duration is { } d && d > TimeSpan.Zero && p > d)
            {
                p = d;
            }

            fields.Add(new("Timeline", $"{FormatPosition(p)} / {track.DisplayDuration}", inline: true));
        }

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

    public static MessageContent Queued(TrackInfoEntity track, int position, long? channelId = null)
    {
        var fields = new List<MessageEmbedField>
        {
            new("Position", $"#{position}", inline: true),
            new("Duration", track.DisplayDuration, inline: true),
            new("Source", track.Source, inline: true),
        };
        if (channelId is long ch)
        {
            fields.Add(new("Channel", $"{ch}", inline: true));
        }

        return Build(
            "Queued",
            Escape(track.Title),
            ColorOk,
            track.ThumbnailUrl,
            track.WebpageUrl,
            includeControls: false,
            fields);
    }

    public static MessageContent UpNext(TrackInfoEntity track, int secondsRemaining = 10, long? channelId = null)
    {
        var fields = new List<MessageEmbedField>
        {
            new("Starts in", $"{secondsRemaining}s", inline: true),
            new("Duration", track.DisplayDuration, inline: true),
            new("Source", track.Source, inline: true),
        };
        if (channelId is long ch)
        {
            fields.Add(new("Channel", $"{ch}", inline: true));
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
            return Build("Queue", "Queue is empty.", ColorInfo, thumbnailUrl: null, url: null, includeControls: false);
        }

        var fields = new List<MessageEmbedField>();
        if (current is not null)
        {
            fields.Add(new("Now", Escape(current.Track.Title), inline: true));
            fields.Add(new("Duration", current.Track.DisplayDuration, inline: true));
            fields.Add(new("Channel", $"{current.Target.ChannelId}", inline: true));
        }

        for (var i = 0; i < upcoming.Count; i++)
        {
            var item = upcoming[i];
            fields.Add(new($"#{i + 1}", Escape(item.Track.Title), inline: true));
            fields.Add(new("Duration", item.Track.DisplayDuration, inline: true));
            fields.Add(new("Channel", $"{item.Target.ChannelId}", inline: true));
        }

        var description = current is null
            ? $"{upcoming.Count} track(s) waiting"
            : $"{upcoming.Count} track(s) waiting after now playing";

        return Build(
            "Queue",
            description,
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
            : $"{mode} · {channelLabel}";

    public static MessageContent NotAllowed(string description)
        => Error("Not allowed", description);

    private static string FormatPosition(TimeSpan position)
    {
        position = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        return position.TotalHours >= 1
            ? position.ToString(@"h\:mm\:ss")
            : position.ToString(@"m\:ss");
    }

    public static MessageContent Status(string title, string description)
        => Build(title, description, ColorInfo, thumbnailUrl: null, url: null, includeControls: false);

    public static MessageContent Ok(string title, string description)
        => Build(title, description, ColorOk, thumbnailUrl: null, url: null, includeControls: false);

    public static MessageContent Error(string title, string description)
        => Build(title, description, ColorError, thumbnailUrl: null, url: null, includeControls: false);

    /// <summary>Generic reply for unexpected exceptions (no internal details).</summary>
    public static MessageContent Awkward()
        => Error(
            "Well, this is awkward...",
            "Something took an unexpected coffee break.");

    public static MessageContent RateLimited()
        => Status(
            "Taking a quick breather! You’re typing faster than we can think.",
            "Please take a 30-second breather and try again.");

    public static MessageContent Help(string prefix)
    {
        var p = prefix;
        var fields = new List<MessageEmbedField>
        {
            new("Voice", $"{p}play [#voice] <url|query>"),
            new("Streaming", $"{p}stream [#stream] <url|query>"),
            new("Controls", $"{p}skip · {p}stop · {p}queue · {p}np"),
            new("DJ", $"{p}setdj @role|roleId|none · {p}settings"),
        };

        return Build(
            "Mezube help",
            "Mention the target with a channel hashtag.\n E.g. !play #music never gonna give you up.",
            ColorInfo,
            thumbnailUrl: null,
            url: null,
            includeControls: false,
            fields);
    }

    public static MessageContent Text(string title, string description)
        => Status(title, description);

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
        string? stopButtonId = null)
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

            if (!string.IsNullOrWhiteSpace(skipButtonId) && !string.IsNullOrWhiteSpace(stopButtonId))
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
    /// Dense multi-cell EQ: each Mezon pool cell holds several thin bars with a
    /// continuous rainbow hue across the whole viz (not one solid color per cell).
    /// Frame names: <c>c{col}_h{1..10}.png</c> — keep in sync with Assets/viz generator.
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
                seq[s] = $"c{c}_h{height}.png";
            }

            pool[c] = seq;
        }

        return pool;
    }
}
