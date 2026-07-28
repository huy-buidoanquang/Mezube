using System.Net;

namespace Mezube.Stn;

/// <summary>STN voice v2/WHIP API failure with HTTP status and response body for UX mapping.</summary>
public sealed class StnVoiceException : InvalidOperationException
{
    public StnVoiceException(string operation, HttpStatusCode statusCode, string body)
        : base($"{operation} {(int)statusCode}: {body}")
    {
        Operation = operation;
        StatusCode = statusCode;
        Body = body ?? string.Empty;
    }

    public string Operation { get; }
    public HttpStatusCode StatusCode { get; }
    public string Body { get; }

    public bool IsConflict => StatusCode == HttpStatusCode.Conflict;
    public bool IsCapacityExceeded => StatusCode == HttpStatusCode.TooManyRequests;
}
