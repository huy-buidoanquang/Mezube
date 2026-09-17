using System.Buffers.Binary;

namespace Mezube.Sfu;

internal sealed class OggOpusReader
{
    private const int MaxPacketBytes = 8 * 1024 * 1024;
    private static readonly byte[] OpusHead = "OpusHead"u8.ToArray();
    private static readonly byte[] OpusTags = "OpusTags"u8.ToArray();

    private readonly Stream _stream;
    private readonly Queue<byte[]> _packets = new();
    private readonly List<byte> _packet = new();
    private uint? _serial;
    private uint _pageSequence;
    private bool _hasPage;
    private bool _headSeen;
    private bool _tagsSeen;

    public OggOpusReader(Stream stream) => _stream = stream;

    public async Task<byte[]?> ReadPacketAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_packets.Count > 0)
            {
                var packet = _packets.Dequeue();
                if (packet.AsSpan().StartsWith(OpusHead))
                {
                    if (packet.Length < 19 || packet[8] != 1 || packet[9] != 2 || packet[18] != 0
                        || BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12, 4)) != 48000)
                    {
                        throw new InvalidDataException("Invalid Ogg Opus header.");
                    }

                    _headSeen = true;
                    continue;
                }

                if (packet.AsSpan().StartsWith(OpusTags))
                {
                    if (!_headSeen)
                    {
                        throw new InvalidDataException("Ogg Opus tags appeared before the header.");
                    }

                    _tagsSeen = true;
                    continue;
                }

                if (!_headSeen || !_tagsSeen || packet.Length == 0)
                {
                    throw new InvalidDataException("Ogg stream does not contain a valid Opus payload.");
                }

                return packet;
            }

            var page = await ReadPageAsync(cancellationToken).ConfigureAwait(false);
            if (page is null)
            {
                if (_packet.Count != 0)
                {
                    throw new InvalidDataException("Truncated Ogg Opus packet.");
                }

                if (!_headSeen || !_tagsSeen)
                {
                    throw new InvalidDataException("Ogg stream ended before Opus headers.");
                }

                return null;
            }

            ParsePage(page.Value.Lacing, page.Value.Body, page.Value.Continued);
        }
    }

    private async Task<(byte[] Lacing, byte[] Body, bool Continued)?> ReadPageAsync(CancellationToken cancellationToken)
    {
        var header = new byte[27];
        var first = await ReadAtMostAsync(header, cancellationToken).ConfigureAwait(false);
        if (first == 0)
        {
            return null;
        }

        if (first != header.Length)
        {
            throw new InvalidDataException("Truncated Ogg page header.");
        }

        if (header[0] != (byte)'O' || header[1] != (byte)'g' || header[2] != (byte)'g' || header[3] != (byte)'S'
            || header[4] != 0 || (header[5] & 0xf8) != 0)
        {
            throw new InvalidDataException("Invalid Ogg page.");
        }

        var serial = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(14, 4));
        var sequence = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(18, 4));
        if (!_hasPage)
        {
            _serial = serial;
            _hasPage = true;
        }
        else if (_serial != serial || sequence != _pageSequence + 1)
        {
            throw new InvalidDataException("Ogg page serial or sequence is not continuous.");
        }

        _pageSequence = sequence;
        var lacing = new byte[header[26]];
        await ReadExactlyAsync(lacing, cancellationToken).ConfigureAwait(false);
        var body = new byte[lacing.Sum(size => (int)size)];
        await ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        if (!ValidCrc(header, lacing, body))
        {
            throw new InvalidDataException("Ogg page CRC validation failed.");
        }

        return (lacing, body, (header[5] & 0x01) != 0);
    }

    private void ParsePage(byte[] lacing, byte[] body, bool continued)
    {
        if (continued != (_packet.Count != 0))
        {
            throw new InvalidDataException("Ogg continued-packet flag is inconsistent.");
        }

        var offset = 0;
        foreach (var segmentSize in lacing)
        {
            var end = checked(offset + segmentSize);
            _packet.AddRange(body.AsSpan(offset, segmentSize).ToArray());
            offset = end;
            if (_packet.Count > MaxPacketBytes)
            {
                throw new InvalidDataException("Ogg Opus packet exceeds the size limit.");
            }

            if (segmentSize < 255)
            {
                _packets.Enqueue(_packet.ToArray());
                _packet.Clear();
            }
        }
    }

    private static bool ValidCrc(byte[] header, byte[] lacing, byte[] body)
    {
        var expected = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(22, 4));
        var copy = header.ToArray();
        copy.AsSpan(22, 4).Clear();
        var crc = OggCrc(0, copy);
        crc = OggCrc(crc, lacing);
        crc = OggCrc(crc, body);
        return crc == expected;
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

    private async Task<int> ReadAtMostAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await _stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private async Task ReadExactlyAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await _stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidDataException("Truncated Ogg page.");
            }

            offset += read;
        }
    }
}

internal static class OpusPacket
{
    public static uint GetDurationSamples(ReadOnlySpan<byte> packet)
    {
        if (packet.Length == 0)
        {
            throw new InvalidDataException("Empty Opus packet.");
        }

        var config = packet[0] >> 3;
        var frameCode = packet[0] & 0x03;
        var durationMs = config < 12
            ? new[] { 10d, 20d, 40d, 60d }[config & 3]
            : 2.5d * (1 << (config & 3));
        var frameCount = frameCode switch
        {
            0 => 1,
            1 or 2 => 2,
            3 when packet.Length >= 2 && (packet[1] & 0x3f) != 0 => packet[1] & 0x3f,
            _ => throw new InvalidDataException("Invalid Opus packet frame count.")
        };
        var samples = (uint)(durationMs * 48 * frameCount);
        if (samples == 0 || samples > 5760)
        {
            throw new InvalidDataException("Opus packet duration is outside the supported range.");
        }

        return samples;
    }
}
