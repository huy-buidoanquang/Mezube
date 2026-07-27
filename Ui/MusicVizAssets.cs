using Mezon.Net.Sdk;
using Mezube.Bot;
using Mezube.Media;
using Microsoft.Extensions.Logging;

namespace Mezube.Ui;

/// <summary>
/// Resolves equalizer sprite URLs for embed Animation (type 6).
/// Uses env URLs when set; otherwise uploads bundled <c>Assets/viz</c> once after login.
/// </summary>
public sealed class MusicVizAssets
{
    private readonly BotOptions _options;
    private readonly MezonCdnUploader _uploader;
    private readonly ILogger<MusicVizAssets> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _imageUrl;
    private string? _positionUrl;
    private bool _ready;

    public MusicVizAssets(BotOptions options, MezonCdnUploader uploader, ILogger<MusicVizAssets> logger)
    {
        _options = options;
        _uploader = uploader;
        _logger = logger;
        _imageUrl = NullIfEmpty(options.VizImageUrl);
        _positionUrl = NullIfEmpty(options.VizPositionUrl);
        _ready = _imageUrl is not null && _positionUrl is not null;
    }

    public string? ImageUrl => _imageUrl;
    public string? PositionUrl => _positionUrl;
    public bool IsReady => _ready && !string.IsNullOrWhiteSpace(_imageUrl) && !string.IsNullOrWhiteSpace(_positionUrl);

    public async Task EnsureAsync(MezonClient client, CancellationToken cancellationToken = default)
    {
        if (IsReady)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsReady)
            {
                return;
            }

            var imagePath = ResolveBundledPath("equalizer.png");
            var jsonPath = ResolveBundledPath("equalizer.json");
            if (imagePath is null || jsonPath is null)
            {
                _logger.LogWarning(
                    "Music viz assets missing (set MEZUBE_VIZ_IMAGE_URL / MEZUBE_VIZ_POSITION_URL or ship Assets/viz).");
                return;
            }

            _imageUrl = await _uploader.UploadAsync(client, imagePath, "image/png", cancellationToken)
                .ConfigureAwait(false);
            _positionUrl = await _uploader.UploadAsync(client, jsonPath, "application/json", cancellationToken)
                .ConfigureAwait(false);
            _options.VizImageUrl = _imageUrl;
            _options.VizPositionUrl = _positionUrl;
            _ready = true;
            _logger.LogInformation("Music viz uploaded image={Image} position={Position}", _imageUrl, _positionUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to prepare music viz assets; !np will skip animation");
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? ResolveBundledPath(string fileName)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "viz", fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "Assets", "viz", fileName),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
