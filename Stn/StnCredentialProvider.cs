using System.Reflection;
using Mezube.Bot;
using Microsoft.Extensions.Logging;

namespace Mezube.Stn;

public sealed class StnCredentialProvider
{
    private readonly BotOptions _options;
    private readonly StreamingChannelSinkHolder _holder;
    private readonly ILogger<StnCredentialProvider> _logger;
    private int _sdkCompatibilityWarningLogged;

    public StnCredentialProvider(
        BotOptions options,
        StreamingChannelSinkHolder holder,
        ILogger<StnCredentialProvider> logger)
    {
        _options = options;
        _holder = holder;
        _logger = logger;
    }

    public async Task<StnCredential> GetPrimaryAsync()
    {
        var client = _holder.GetClient();
        if (_options.StnAuthMode == StnAuthMode.Auto)
        {
            var method = client.GetType().GetMethod(
                "GetSessionIdAsync",
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);

            if (method?.Invoke(client, null) is Task<string> sessionIdTask)
            {
                var sessionId = await sessionIdTask.ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(sessionId))
                {
                    return new StnCredential(StnCredentialKind.SessionId, sessionId);
                }
            }
            else
            {
                // Compatibility bridge for 1.2.1. GetAuthTokenAsync refreshes the session first;
                // the SDK's internal engine then exposes the current ISession object.
                var refreshedJwt = await client.GetAuthTokenAsync().ConfigureAwait(false);
                var engine = client.GetType()
                    .GetProperty("Engine", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(client);
                var session = engine?.GetType()
                    .GetProperty("CurrentSession", BindingFlags.Instance | BindingFlags.Public)
                    ?.GetValue(engine);
                var legacySessionId = session?.GetType()
                    .GetProperty("SessionId", BindingFlags.Instance | BindingFlags.Public)
                    ?.GetValue(session) as string;
                if (!string.IsNullOrWhiteSpace(legacySessionId))
                {
                    if (Interlocked.Exchange(ref _sdkCompatibilityWarningLogged, 1) == 0)
                    {
                        _logger.LogInformation(
                            "STN SID is using the Mezon.Net.Sdk 1.2.1 compatibility bridge; upgrade to SDK 1.2.2");
                    }

                    return new StnCredential(StnCredentialKind.SessionId, legacySessionId);
                }

                return new StnCredential(StnCredentialKind.Jwt, refreshedJwt);
            }
        }

        return await GetJwtAsync().ConfigureAwait(false);
    }

    public async Task<StnCredential> GetJwtAsync()
    {
        var token = await _holder.GetClient().GetAuthTokenAsync().ConfigureAwait(false);
        return new StnCredential(StnCredentialKind.Jwt, token);
    }
}
