namespace Mezube.Media;

public sealed class AudioTooLargeException : Exception
{
    public AudioTooLargeException(string title, long sizeBytes, long maxBytes)
        : base($"Audio '{title}' is {sizeBytes} bytes; max allowed is {maxBytes}.")
    {
        Title = title;
        SizeBytes = sizeBytes;
        MaxBytes = maxBytes;
    }

    public string Title { get; }
    public long SizeBytes { get; }
    public long MaxBytes { get; }
}
