namespace Mezube.Stn;

/// <summary>
/// STN admission control rejects work instead of exhausting memory: the WS door
/// answers with these strings (constant/constant.go ERR_MAX_*, ERR_MEMORY_PRESSURE).
/// </summary>
public static class StnServerLoad
{
    private static readonly string[] Markers =
    [
        "max subscribers",
        "memory pressure",
        "max concurrent",
        "service shutting down",
    ];

    public static bool MentionsCapacity(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var marker in Markers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
