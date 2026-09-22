namespace Mezube.Sfu;

internal static class SfuReconnectPolicy
{
    public static TimeSpan GetDelay(
        int attempt,
        int baseDelayMs,
        int maxDelayMs,
        int jitterMs)
    {
        if (attempt < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt));
        }

        var cappedBase = Math.Max(1, baseDelayMs);
        var cappedMaximum = Math.Max(cappedBase, maxDelayMs);
        var exponent = Math.Min(attempt - 1, 30);
        var exponential = Math.Min(
            cappedMaximum,
            cappedBase * (1L << exponent));
        var jitter = jitterMs > 0
            ? Random.Shared.NextInt64(0, (long)jitterMs + 1)
            : 0L;
        var delay = Math.Min(cappedMaximum, exponential + jitter);
        return TimeSpan.FromMilliseconds(delay);
    }
}
