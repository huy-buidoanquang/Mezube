using Mezube.Application;
using Mezube.Bot;
using Mezube.Infrastructure.Persistence;
using Mezube.Infrastructure.Persistence.Sqlite;
using Mezube.Media;
using Mezube.Music;
using Mezube.Playback;
using Mezube.Stn;
using Mezube.Ui;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mezube;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"))
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")))
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "prod");
        }

        var builder = Host.CreateApplicationBuilder(args);
        builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);
        builder.Configuration.AddJsonFile(
            $"appsettings.{builder.Environment.EnvironmentName}.local.json",
            optional: true,
            reloadOnChange: true);

        var options = BotOptions.FromConfiguration(builder.Configuration);
        options.Validate();
        builder.Services.AddSingleton(options);
        PlayerMessageBuilder.Configure(options);
        ConfigureLogging(builder.Logging, builder.Configuration, options);

        builder.Services.AddHttpClient<StnRestClientV2>();
        builder.Services.AddHttpClient<StnWhipClient>();
        builder.Services.AddHttpClient(nameof(MezonCdnUploader));
        builder.Services.AddSingleton<StnStreamingSessionManager>();
        builder.Services.AddSingleton<StreamingChannelSinkHolder>();
        builder.Services.AddSingleton<VoiceChannelSinkHolder>();
        builder.Services.AddSingleton<YtDlpProcessor>();
        builder.Services.AddSingleton<FfmpegProcessor>();
        builder.Services.AddSingleton<WhipFfmpegPublisher>();
        builder.Services.AddSingleton<MezonCdnUploader>();
        builder.Services.AddSingleton<MusicVizAssets>();

        builder.Services.AddSingleton<SqliteDbConnectionFactory>();
        builder.Services.AddSingleton<ITrackRepository, SqliteTrackRepository>();
        builder.Services.AddSingleton<IClanSettingsRepository, SqliteClanSettingsRepository>();
        builder.Services.AddSingleton<ITrackLibraryService, TrackLibraryService>();
        builder.Services.AddSingleton<IClanSettingsService, ClanSettingsService>();

        builder.Services.AddSingleton<PlayableMediaProcessor>();
        builder.Services.AddSingleton<TrackPrepService>();
        builder.Services.AddSingleton<YoutubeTrackResolver>();
        builder.Services.AddSingleton<DirectUrlTrackResolver>();
        builder.Services.AddSingleton<ITrackResolver>(sp =>
            new CompositeTrackResolver(
            [
                sp.GetRequiredService<YoutubeTrackResolver>(),
                sp.GetRequiredService<DirectUrlTrackResolver>(),
            ]));
        builder.Services.AddSingleton<BindStore>();
        builder.Services.AddSingleton<StreamingChannelSink>();
        builder.Services.AddSingleton<VoiceChannelSink>();
        builder.Services.AddSingleton<PlaybackAccess>();
        builder.Services.AddSingleton<MusicPlayer>();
        builder.Services.AddHostedService<MezubeBot>();

        var host = builder.Build();
        RegisterMediaCleanup(host);
        await host.RunAsync().ConfigureAwait(false);
    }

    private static void ConfigureLogging(
        ILoggingBuilder logging,
        IConfiguration configuration,
        BotOptions options)
    {
        logging.ClearProviders();
        logging.AddConfiguration(configuration.GetSection("Logging"));
        logging.SetMinimumLevel(ParseLogLevel(configuration["Logging:LogLevel:Default"], LogLevel.Information));
        logging.AddFilter("Microsoft", ParseLogLevel(configuration["Logging:LogLevel:Microsoft"], LogLevel.Warning));
        logging.AddFilter("System", ParseLogLevel(configuration["Logging:LogLevel:System"], LogLevel.Warning));
        logging.AddFilter("Mezube", ParseLogLevel(configuration["Logging:LogLevel:Mezube"], LogLevel.Information));
        logging.AddFilter("Mezon", (LogLevel)(int)options.MezonNetLogLevel);
        logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
            o.IncludeScopes = true;
        });
    }

    private static LogLevel ParseLogLevel(string? text, LogLevel fallback)
        => !string.IsNullOrWhiteSpace(text) && Enum.TryParse(text, ignoreCase: true, out LogLevel level)
            ? level
            : fallback;

    private static void RegisterMediaCleanup(IHost host)
    {
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var publisher = host.Services.GetRequiredService<WhipFfmpegPublisher>();
        var streamingSessions = host.Services.GetRequiredService<StnStreamingSessionManager>();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Mezube.MediaCleanup");

        lifetime.ApplicationStopping.Register(() =>
        {
            try
            {
                logger.LogDebug("Stopping all active WHIP publishers during host shutdown");
                publisher.StopAllAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to stop all WHIP publishers during shutdown");
            }

            try
            {
                logger.LogDebug("Disposing all STN streaming sessions during host shutdown");
                streamingSessions.DisposeAllAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to dispose STN streaming sessions during shutdown");
            }
        });
    }
}
