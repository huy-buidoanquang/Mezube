namespace Mezube.Stn;

public enum StnSessionState
{
    Disconnected,
    Connecting,
    Ready,
    Invalidated,
    Disposing,
}

public enum StnCredentialKind
{
    Jwt,
    SessionId,
}

public enum StnAuthMode
{
    Auto,
    Jwt,
}

public readonly record struct StnCredential(StnCredentialKind Kind, string Value);

public sealed class StnConnectionException : Exception
{
    public StnConnectionException(
        string message,
        string phase,
        bool retryable,
        int? statusCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Phase = phase;
        Retryable = retryable;
        StatusCode = statusCode;
    }

    public string Phase { get; }
    public bool Retryable { get; }
    public int? StatusCode { get; }

    public bool AllowsJwtFallback
        => StatusCode is 200 or 401 or 403;
}

public sealed class StnPublisherException : Exception
{
    public StnPublisherException(
        string message,
        string phase,
        string? code = null,
        bool retryable = false,
        string? connectionId = null)
        : base(message)
    {
        Phase = phase;
        Code = code;
        Retryable = retryable;
        ConnectionId = connectionId;
    }

    public string Phase { get; }
    public string? Code { get; }
    public bool Retryable { get; }
    public string? ConnectionId { get; }
}
