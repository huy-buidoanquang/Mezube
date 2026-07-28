namespace Mezube.Stn;

/// <summary>Derives REST/WS endpoints from a single STN origin.</summary>
public static class StnUrl
{
    public static string NormalizeBase(string url)
    {
        var baseUrl = url.Trim().TrimEnd('/');
        // Allow pasting full paths; strip known suffixes.
        if (baseUrl.EndsWith("/ws", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = baseUrl[..^3].TrimEnd('/');
        }

        const string play = "/api/playmedia";
        const string stop = "/api/stopmedia";
        const string playV2 = "/api/v2/voice/play";
        const string stopV2 = "/api/v2/voice/stop";
        const string statusV2 = "/api/v2/voice/status";
        const string whipStart = "/api/v2/voice/whip/start";
        const string whipStop = "/api/v2/voice/whip/stop";
        const string whipStatus = "/api/v2/voice/whip/status";
        if (baseUrl.EndsWith(play, StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = baseUrl[..^play.Length].TrimEnd('/');
        }
        else if (baseUrl.EndsWith(stop, StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = baseUrl[..^stop.Length].TrimEnd('/');
        }
        else if (baseUrl.EndsWith(playV2, StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = baseUrl[..^playV2.Length].TrimEnd('/');
        }
        else if (baseUrl.EndsWith(stopV2, StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = baseUrl[..^stopV2.Length].TrimEnd('/');
        }
        else if (baseUrl.EndsWith(statusV2, StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = baseUrl[..^statusV2.Length].TrimEnd('/');
        }
        else if (baseUrl.EndsWith(whipStart, StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = baseUrl[..^whipStart.Length].TrimEnd('/');
        }
        else if (baseUrl.EndsWith(whipStop, StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = baseUrl[..^whipStop.Length].TrimEnd('/');
        }
        else if (baseUrl.EndsWith(whipStatus, StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = baseUrl[..^whipStatus.Length].TrimEnd('/');
        }

        return baseUrl;
    }

    [Obsolete("Legacy URL_INPUT endpoint. Mezube uses VoiceV2PlayUri.")]
    public static Uri PlayMediaUri(string baseUrl)
        => new(NormalizeBase(baseUrl) + "/api/playmedia", UriKind.Absolute);

    [Obsolete("Legacy URL_INPUT endpoint. Mezube uses VoiceV2StopUri.")]
    public static Uri StopMediaUri(string baseUrl)
        => new(NormalizeBase(baseUrl) + "/api/stopmedia", UriKind.Absolute);

    public static Uri VoiceV2PlayUri(string baseUrl)
        => new(NormalizeBase(baseUrl) + "/api/v2/voice/play", UriKind.Absolute);

    public static Uri VoiceV2StopUri(string baseUrl)
        => new(NormalizeBase(baseUrl) + "/api/v2/voice/stop", UriKind.Absolute);

    public static Uri VoiceV2StatusUri(string baseUrl)
        => new(NormalizeBase(baseUrl) + "/api/v2/voice/status", UriKind.Absolute);

    public static Uri VoiceWhipStartUri(string baseUrl)
        => new(NormalizeBase(baseUrl) + "/api/v2/voice/whip/start", UriKind.Absolute);

    public static Uri VoiceWhipStopUri(string baseUrl)
        => new(NormalizeBase(baseUrl) + "/api/v2/voice/whip/stop", UriKind.Absolute);

    public static Uri VoiceWhipStatusUri(string baseUrl)
        => new(NormalizeBase(baseUrl) + "/api/v2/voice/whip/status", UriKind.Absolute);

    public static string WebSocketBase(string baseUrl)
    {
        var http = NormalizeBase(baseUrl);
        string ws;
        if (http.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            ws = "wss://" + http["https://".Length..];
        }
        else if (http.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            ws = "ws://" + http["http://".Length..];
        }
        else if (http.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)
                 || http.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
        {
            ws = http;
        }
        else
        {
            ws = "wss://" + http.TrimStart('/');
        }

        return ws.TrimEnd('/') + "/ws";
    }
}
