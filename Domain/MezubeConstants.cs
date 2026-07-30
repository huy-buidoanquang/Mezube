namespace Mezube.Domain;

/// <summary>Shared limits and fixed user-facing titles for Mezube.</summary>
public static class MezubeConstants
{
    public const int MaxPrepConcurrency = 64;
    public const int MaxConcurrentPlayback = 64;
    public const int MaxQueuePerClan = 20;
    public const long MaxAudioBytes = 100L * 1024 * 1024;
    public const int InterTrackDelayMs = 2000;

    public const string TitleCopyrightBlocked =
        "Copyright strikes again! I can’t play this song right now.";

    public const string TitleQueueFull =
        "Everyone wants a piece of me today! Please take a number and hold tight";

    public const string TitlePlaybackSlotsFull =
        "All stages are taken! Wait for a clan to finish, then try again.";
}
