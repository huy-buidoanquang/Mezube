namespace Mezube.Stn;

/// <summary>Derives the streaming WebSocket endpoint from a single STN origin.</summary>
public static class StnUrl
{
    private static readonly string[] KnownSuffixes =
    [
        "/ws",
        "/api/playmedia",
        "/api/stopmedia",
        "/api/v2/voice/play",
        "/api/v2/voice/stop",
        "/api/v2/voice/status",
        "/api/v2/voice/whip/start",
        "/api/v2/voice/whip/stop",
        "/api/v2/voice/whip/status",
        "/api/voice/play",
        "/api/voice/stop",
        "/api/voice/status",
        "/api/voice/metrics",
        "/api/whip/start",
        "/api/whip/stop",
        "/api/whip/status",
    ];

    public static string NormalizeBase(string url)
    {
        var baseUrl = url.Trim().TrimEnd('/');
        foreach (var suffix in KnownSuffixes)
        {
            if (baseUrl.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return baseUrl[..^suffix.Length].TrimEnd('/');
            }
        }

        return baseUrl;
    }

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
