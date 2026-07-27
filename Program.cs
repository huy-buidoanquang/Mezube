using DotNetEnv;
using Mezube.Bot;
using Mezube.Domain.Persistence;
using Mezube.Media;
using Mezube.Music;
using Mezube.Playback;
using Mezube.Stn;
using Mezube.Ui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mezube;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        LoadEnvFiles();

        var builder = Host.CreateApplicationBuilder(args);

        var options = BotOptions.FromConfiguration(builder.Configuration);
        builder.Services.AddSingleton(options);
        PlayerMessageBuilder.Configure(options);
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter("System", LogLevel.Warning);
        builder.Logging.AddFilter("Mezube", LogLevel.Trace);
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
            o.IncludeScopes = true;
        });

        builder.Services.AddHttpClient<StnRestClientV2>();
        builder.Services.AddHttpClient(nameof(MezonCdnUploader));
        builder.Services.AddSingleton<StnSocketClient>();
        builder.Services.AddSingleton<StreamingChannelSinkHolder>();
        builder.Services.AddSingleton<VoiceChannelSinkHolder>();
        builder.Services.AddSingleton<YtDlpRunner>();
        builder.Services.AddSingleton<FfmpegRunner>();
        builder.Services.AddSingleton<MezonCdnUploader>();
        builder.Services.AddSingleton<MusicVizAssets>();
        builder.Services.AddSingleton<ITrackDb, SqliteTrackDb>();
        builder.Services.AddSingleton<PlayableMediaPreparer>();
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
        builder.Services.AddSingleton<MusicPlayer>();
        builder.Services.AddHostedService<MezubeBot>();

        var host = builder.Build();
        await host.RunAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Loads <c>.env</c> (selector) then <c>.env.{MEZUBE_ENV}</c>, then optional <c>.env.local</c>.
    /// Default profile is <c>prod</c>. Switch with <c>MEZUBE_ENV=dev</c> in <c>.env</c>.
    /// </summary>
    private static void LoadEnvFiles()
    {
        var searchRoots = new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        TryLoadEnv(searchRoots, ".env");

        var profile = Environment.GetEnvironmentVariable("MEZUBE_ENV")?.Trim();
        if (string.IsNullOrWhiteSpace(profile))
        {
            profile = "prod";
        }

        Environment.SetEnvironmentVariable("MEZUBE_ENV", profile);
        if (!TryLoadEnv(searchRoots, $".env.{profile}"))
        {
            Console.Error.WriteLine(
                $"Warning: .env.{profile} not found (MEZUBE_ENV={profile}). Falling back to process env only.");
        }

        TryLoadEnv(searchRoots, ".env.local");
        Console.WriteLine($"Loaded env profile: {profile}");
    }

    private static bool TryLoadEnv(IEnumerable<string> roots, string fileName)
    {
        foreach (var root in roots)
        {
            var path = Path.Combine(root, fileName);
            if (!File.Exists(path))
            {
                continue;
            }

            Env.Load(path);
            return true;
        }

        return false;
    }
}
