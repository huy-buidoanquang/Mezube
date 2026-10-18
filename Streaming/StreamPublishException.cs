namespace Mezube.Streaming;

public enum StreamFailureKind
{
    Unavailable,
    ChannelBusy,
    Denied,
}

public sealed class StreamPublishException : Exception
{
    public StreamPublishException(StreamFailureKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    public StreamFailureKind Kind { get; }

    internal static StreamFailureKind ForWhipStatus(int status) => status switch
    {
        409 => StreamFailureKind.ChannelBusy,
        401 or 408 or 429 => StreamFailureKind.Unavailable,
        >= 400 and < 500 => StreamFailureKind.Denied,
        _ => StreamFailureKind.Unavailable,
    };

    internal static StreamFailureKind ForApiCode(int code)
        => code is 3 or 5 or 7 or 400 or 403 or 404 ? StreamFailureKind.Denied : StreamFailureKind.Unavailable;
}
