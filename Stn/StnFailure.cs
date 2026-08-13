using System.Net;

namespace Mezube.Stn;

internal static class StnFailure
{
    public static Exception From(string operation, HttpStatusCode statusCode, string body)
    {
        var stn = new StnVoiceException(operation, statusCode, body);
        if (stn.IsUnavailable || stn.IsCapacityExceeded || StnServerLoad.MentionsCapacity(body))
        {
            return new StnCapacityException(stn.Message, stn);
        }

        return stn;
    }
}
