using Mezon.Net.Sdk.Entities;

namespace Mezube.Helpers;

/// <summary>
/// Tracks the latest bot reply for the current command so exception middleware
/// can update Searching / prior reply instead of posting a second Awkward message.
/// </summary>
public static class CommandReplyTracker
{
    private static readonly AsyncLocal<Slot?> Current = new();

    public sealed class Slot
    {
        public long MessageId { get; init; }
        public uint? CreateTimeSeconds { get; init; }
        public TextChannel Channel { get; init; } = null!;
    }

    public static void Remember(long messageId, uint? createTimeSeconds, TextChannel channel)
        => Current.Value = new Slot
        {
            MessageId = messageId,
            CreateTimeSeconds = createTimeSeconds,
            Channel = channel,
        };

    public static Slot? Peek() => Current.Value;

    public static void Clear() => Current.Value = null;
}
