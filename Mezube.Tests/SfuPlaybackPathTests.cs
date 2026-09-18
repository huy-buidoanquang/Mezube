using Mezube.Bot;
using Mezube.Domain.Entities;
using Mezube.Media;
using Mezube.Sfu;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mezube.Tests;

public sealed class SfuPlaybackPathTests
{
    [Theory]
    [InlineData("https://cdn.example.test/song.normalized.ogg", true)]
    [InlineData("https://cdn.example.test/song.opus", true)]
    [InlineData("https://cdn.example.test/song.webm", false)]
    [InlineData("https://cdn.example.test/song.mp3", false)]
    [InlineData("", false)]
    public void Media_source_parses_http_ogg_opus_only(string url, bool expected)
        => Assert.Equal(expected, SfuMediaSource.TryParse(url, out _));

    [Fact]
    public void Media_source_does_not_treat_missing_local_ogg_as_ready()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".normalized.ogg");
        var track = new TrackInfoEntity
        {
            Title = "t",
            MediaUrl = "https://cdn.example.test/not-ready.mp3",
            LocalMediaPath = path,
        };

        Assert.False(File.Exists(path));
        Assert.False(SfuMediaSource.TryParse(track, out _));
    }

    [Fact]
    public void Media_source_prefers_existing_local_ogg_over_http_url()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mezube-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "track.normalized.ogg");
        try
        {
            File.WriteAllBytes(path, [1, 2, 3]);
            var track = new TrackInfoEntity
            {
                Title = "t",
                MediaUrl = "https://cdn.example.test/other.normalized.ogg",
                LocalMediaPath = path,
            };

            Assert.True(SfuMediaSource.TryParse(track, out var source));
            Assert.True(source.IsLocal);
            Assert.Equal(path, source.LocalPath);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Prepared_cache_path_is_stable_and_under_prepared_folder()
    {
        var path = PreparedAudioCache.GetPath("temp", "youtube", "abc def/id");
        Assert.Contains(Path.Combine("temp", "prepared"), path);
        Assert.EndsWith(".normalized.ogg", path);
        Assert.DoesNotContain(" ", Path.GetFileName(path));
        Assert.DoesNotContain("/", Path.GetFileName(path));
        Assert.Equal(path, PreparedAudioCache.GetPath("temp", "youtube", "abc def/id"));
    }

    [Fact]
    public async Task Prepare_sfu_ogg_copies_already_compatible_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mezube-prep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var input = Path.Combine(dir, "source.ogg");
            var output = Path.Combine(dir, "out.normalized.ogg");
            await File.WriteAllBytesAsync(input, CompatibleOgg());

            var ffmpeg = new FfmpegProcessor(
                new BotOptions { TempDir = dir, FfmpegPath = Path.Combine(dir, "ffmpeg-missing") },
                NullLogger<FfmpegProcessor>.Instance);

            var prepared = await ffmpeg.PrepareSfuOggAsync(input, output, CancellationToken.None);
            Assert.Equal(output, prepared);
            Assert.True(await OggOpusReader.IsSfuCompatibleFileAsync(output, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static byte[] CompatibleOgg()
    {
        using var stream = new MemoryStream();
        // Reuse the contract test builder via a file written by SfuOggOpusTests isn't accessible.
        // Minimal valid stereo 48 kHz stream is produced the same way as SfuOggOpusTests.
        return SfuOggTestFile.Stereo48k(new byte[] { 0x08, 0x01 });
    }
}

internal static class SfuOggTestFile
{
    public static byte[] Stereo48k(byte[] payload)
    {
        var head = new byte[19];
        "OpusHead"u8.CopyTo(head.AsSpan());
        head[8] = 1;
        head[9] = 2;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12, 4), 48000);
        var tags = "OpusTags\0\0\0\0"u8.ToArray();
        return Concat(
            Page(flags: 2, sequence: 0, head),
            Page(flags: 0, sequence: 1, tags),
            Page(flags: 4, sequence: 2, payload));
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var length = parts.Sum(p => p.Length);
        var all = new byte[length];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(all, offset);
            offset += part.Length;
        }

        return all;
    }

    private static byte[] Page(byte flags, uint sequence, byte[] packet)
    {
        var lacing = new List<byte>();
        using var body = new MemoryStream();
        var offset = 0;
        while (packet.Length - offset >= 255)
        {
            lacing.Add(255);
            body.Write(packet, offset, 255);
            offset += 255;
        }

        lacing.Add((byte)(packet.Length - offset));
        body.Write(packet, offset, packet.Length - offset);

        var header = new byte[27];
        "OggS"u8.CopyTo(header.AsSpan());
        header[5] = flags;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(14, 4), 7);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(18, 4), sequence);
        header[26] = (byte)lacing.Count;
        var bodyBytes = body.ToArray();
        var crc = Crc(0, header);
        crc = Crc(crc, lacing.ToArray());
        crc = Crc(crc, bodyBytes);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(22, 4), crc);
        return header.Concat(lacing).Concat(bodyBytes).ToArray();
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            crc ^= (uint)value << 24;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04c11db7 : crc << 1;
            }
        }

        return crc;
    }
}
