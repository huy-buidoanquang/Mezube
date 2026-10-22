using Mezube.Bot;
using Mezube.Media;
using Mezube.Streaming;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mezube.Tests;

public sealed class AtomicPreparedOggTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mezube-atomic-" + Guid.NewGuid().ToString("N"));

    public AtomicPreparedOggTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task EnsureOggAsync_moves_finished_ogg_to_destination()
    {
        var input = Path.Combine(_dir, "mezube_w.ogg");
        var work = Path.Combine(_dir, "mezube_w.normalized.ogg");
        var output = Path.Combine(_dir, "prepared", "youtube_x.normalized.ogg");
        await File.WriteAllBytesAsync(input, StreamOggTestFile.Stereo48k([0x08, 0x01]));

        var result = await PipelineProcessor.EnsureOggAsync(
            Ffmpeg(Path.Combine(_dir, "ffmpeg-missing")),
            input,
            work,
            output,
            CancellationToken.None);

        Assert.Equal(output, result);
        Assert.False(File.Exists(work));
        Assert.True(await OggOpusReader.IsStreamCompatibleFileAsync(output, CancellationToken.None));
    }

    [Fact]
    public async Task EnsureOggAsync_leaves_destination_empty_when_cancelled_mid_encode()
    {
        var input = Path.Combine(_dir, "mezube_w.webm");
        var work = Path.Combine(_dir, "mezube_w.normalized.ogg");
        var output = Path.Combine(_dir, "prepared", "youtube_x.normalized.ogg");
        await File.WriteAllTextAsync(input, "not ogg");
        using var cts = new CancellationTokenSource();

        var task = PipelineProcessor.EnsureOggAsync(Ffmpeg(WriteFakeFfmpeg(hang: true)), input, work, output, cts.Token);
        for (var i = 0; i < 500 && !File.Exists(work); i++)
        {
            await Task.Delay(20);
        }

        Assert.True(File.Exists(work));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task TranscodeToOggAsync_deletes_partial_output_when_ffmpeg_fails()
    {
        var input = Path.Combine(_dir, "in.webm");
        var output = Path.Combine(_dir, "out.normalized.ogg");
        await File.WriteAllTextAsync(input, "not ogg");

        var result = await Ffmpeg(WriteFakeFfmpeg(hang: false)).TranscodeToOggAsync(input, output, CancellationToken.None);

        Assert.Null(result);
        Assert.False(File.Exists(output));
    }

    private FfmpegProcessor Ffmpeg(string path)
        => new(new BotOptions { TempDir = _dir, FfmpegPath = path }, NullLogger<FfmpegProcessor>.Instance);

    private string WriteFakeFfmpeg(bool hang)
    {
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(_dir, "ffmpeg.cmd");
            File.WriteAllText(cmd, string.Join("\r\n",
                "@echo off",
                "if \"%~1\"==\"-version\" exit /b 0",
                ":next",
                "set \"out=%~1\"",
                "shift",
                "if not \"%~1\"==\"\" goto next",
                "echo partial>\"%out%\"",
                hang ? "ping -n 30 127.0.0.1 >nul" : "",
                "exit /b 1",
                ""));
            return cmd;
        }

        var sh = Path.Combine(_dir, "ffmpeg");
        File.WriteAllText(sh, string.Join("\n",
            "#!/bin/sh",
            "[ \"$1\" = \"-version\" ] && exit 0",
            "for a; do out=\"$a\"; done",
            "echo partial > \"$out\"",
            hang ? "sleep 30" : "",
            "exit 1",
            ""));
        File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return sh;
    }
}
