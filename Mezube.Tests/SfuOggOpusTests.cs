using System.Buffers.Binary;
using Mezube.Sfu;

namespace Mezube.Tests;

public sealed class SfuOggOpusTests
{
    [Fact]
    public async Task Reader_handles_headers_and_packet_split_across_pages()
    {
        var payload = Enumerable.Range(0, 300).Select(value => (byte)value).ToArray();
        await using var stream = new MemoryStream(BuildOgg(payload));
        var reader = new OggOpusReader(stream);

        Assert.Equal(payload, await reader.ReadPacketAsync(CancellationToken.None));
        Assert.Null(await reader.ReadPacketAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Reader_rejects_crc_failure()
    {
        var data = BuildOgg(new byte[] { 0x08, 0x01 });
        data[22] ^= 0x01;
        await using var stream = new MemoryStream(data);
        var reader = new OggOpusReader(stream);

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadPacketAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Reader_rejects_truncated_page()
    {
        var data = BuildOgg(new byte[] { 0x08, 0x01 });
        await using var stream = new MemoryStream(data[..^1]);
        var reader = new OggOpusReader(stream);

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadPacketAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(0x00, 480u)]
    [InlineData(0x08, 960u)]
    [InlineData(0x18, 2880u)]
    [InlineData(0x60, 120u)]
    public void Opus_duration_uses_toc_frame_duration(byte toc, uint expectedSamples)
        => Assert.Equal(expectedSamples, OpusPacket.GetDurationSamples(new[] { toc }));

    private static byte[] BuildOgg(byte[] payload)
    {
        var head = new byte[19];
        "OpusHead"u8.CopyTo(head.AsSpan());
        head[8] = 1;
        head[9] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12, 4), 48000);

        var tags = "OpusTags\0\0\0\0"u8.ToArray();
        var pageHead = BuildPage(flags: 2, sequence: 0, head);
        var pageTags = BuildPage(flags: 0, sequence: 1, tags);

        if (payload.Length <= 255)
        {
            return pageHead.Concat(pageTags).Concat(BuildPage(flags: 4, sequence: 2, payload)).ToArray();
        }

        var payloadPages = BuildPage(flags: 0, sequence: 2, new[] { payload[..255] }, completesPacket: false);
        var payloadRemainder = BuildPage(flags: 5, sequence: 3, payload[255..]);

        return pageHead.Concat(pageTags).Concat(payloadPages).Concat(payloadRemainder).ToArray();
    }

    private static byte[] BuildPage(byte flags, uint sequence, params byte[][] packets)
        => BuildPage(flags, sequence, packets, completesPacket: true);

    private static byte[] BuildPage(byte flags, uint sequence, byte[][] packets, bool completesPacket)
    {
        var lacing = new List<byte>();
        using var body = new MemoryStream();
        for (var packetIndex = 0; packetIndex < packets.Length; packetIndex++)
        {
            var packet = packets[packetIndex];
            var offset = 0;
            while (packet.Length - offset >= 255)
            {
                lacing.Add(255);
                body.Write(packet, offset, 255);
                offset += 255;
            }

            if (offset < packet.Length || packet.Length == 0 || packetIndex < packets.Length - 1 || completesPacket)
            {
                lacing.Add((byte)(packet.Length - offset));
                body.Write(packet, offset, packet.Length - offset);
            }
        }

        var header = new byte[27];
        "OggS"u8.CopyTo(header.AsSpan());
        header[4] = 0;
        header[5] = flags;
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(6, 8), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(14, 4), 7);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(18, 4), sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(22, 4), 0);
        header[26] = (byte)lacing.Count;

        var bodyBytes = body.ToArray();
        var crc = OggCrc(0, header);
        crc = OggCrc(crc, lacing.ToArray());
        crc = OggCrc(crc, bodyBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(22, 4), crc);

        return header.Concat(lacing).Concat(bodyBytes).ToArray();
    }

    private static uint OggCrc(uint crc, ReadOnlySpan<byte> data)
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
