namespace Mezube.Stn;

public sealed class StnCapacityException : Exception
{
    public StnCapacityException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
