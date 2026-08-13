namespace Mezube.Media;

public sealed class MediaPrepException : Exception
{
    public MediaPrepException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
