using Mezube.Application;
using Mezube.Domain.Entities;
using Mezube.Helpers;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;

namespace Mezube.Music;

public sealed class DirectUrlTrackResolver : ITrackResolver
{
    private readonly ITrackLibraryService _store;
    private readonly ILogger<DirectUrlTrackResolver> _logger;

    public DirectUrlTrackResolver(ITrackLibraryService store, ILogger<DirectUrlTrackResolver> logger)
    {
        _store = store;
        _logger = logger;
    }

    public bool CanResolve(string query)
    {
        if (!Uri.TryCreate(query.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        if (host.Contains("youtube.com", StringComparison.Ordinal)
            || host.Contains("youtu.be", StringComparison.Ordinal)
            || host.Contains("music.youtube.com", StringComparison.Ordinal)
            || host.Contains("soundcloud.com", StringComparison.Ordinal)
            || host.Contains("spotify.com", StringComparison.Ordinal)
            || host.Equals("spotify.link", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".spotify.link", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    public async Task<TrackInfoEntity?> ResolveAsync(
        string query,
        string? requestedBy,
        CancellationToken cancellationToken = default)
    {
        var url = query.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        if (!await IsPublicHostAsync(uri.IdnHost, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("Rejected direct URL with non-public host {Host}", uri.Host);
            return null;
        }

        // Prefer .ogg for SFU audio when caller passes .mp3 (Komu convention).
        var mediaUrl = url.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
            ? url[..^4] + ".ogg"
            : url;

        var externalId = TrackIdentityHelper.ForDirectUrl(mediaUrl);
        var cached = await _store.TryGetAsync(TrackIdentityHelper.SourceUrl, externalId, cancellationToken)
            .ConfigureAwait(false);
        if (cached is not null)
        {
            _logger.LogDebug("Track store hit url/{Id}", externalId);
            return cached.ToTrackInfo(requestedBy);
        }

        var title = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(uri.AbsolutePath));
        if (string.IsNullOrWhiteSpace(title))
        {
            title = mediaUrl;
        }

        var track = new TrackInfoEntity
        {
            Title = title,
            MediaUrl = mediaUrl,
            WebpageUrl = url,
            RequestedBy = requestedBy,
            Source = TrackIdentityHelper.SourceUrl,
            ExternalId = externalId,
        };

        await _store.UpsertMetadataAsync(
                new TrackEntity
                {
                    Source = TrackIdentityHelper.SourceUrl,
                    ExternalId = externalId,
                    Title = title,
                    WebpageUrl = url,
                    PlayableUrl = null,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return track;
    }

    internal static async Task<bool> IsPublicHostAsync(string host, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                return false;
            }
        }

        return addresses.Length > 0 && addresses.All(IsPublicAddress);
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0
                     || b[0] == 10
                     || (b[0] == 100 && (b[1] & 0xC0) == 64)
                     || (b[0] == 169 && b[1] == 254)
                     || (b[0] == 172 && (b[1] & 0xF0) == 16)
                     || (b[0] == 192 && b[1] == 168)
                     || b[0] >= 224);
        }

        return !(address.Equals(IPAddress.IPv6Any)
                 || address.IsIPv6LinkLocal
                 || address.IsIPv6SiteLocal
                 || address.IsIPv6UniqueLocal
                 || address.IsIPv6Multicast);
    }
}
