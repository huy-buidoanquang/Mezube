namespace Mezube.Bot;

/// <summary>Mezon gateway credentials and transport.</summary>
public sealed class MezonOptions
{
    public long BotId { get; set; }
    public string Token { get; set; } = string.Empty;
    public string ServerKey { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public string Port { get; set; } = string.Empty;
    public bool UseSsl { get; set; } = true;
}
