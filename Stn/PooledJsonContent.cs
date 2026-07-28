using System.Buffers;
using System.Net;
using System.Net.Http.Headers;

namespace Mezube.Stn;

/// <summary>JSON HttpContent backed by ArrayPool — avoids WrittenSpan.ToArray() on every request.</summary>
internal sealed class PooledJsonContent : HttpContent
{
    private static readonly MediaTypeHeaderValue JsonMediaType = new("application/json");

    private byte[]? _rented;
    private readonly int _length;

    public PooledJsonContent(ReadOnlySpan<byte> utf8Json)
    {
        _length = utf8Json.Length;
        _rented = ArrayPool<byte>.Shared.Rent(_length);
        utf8Json.CopyTo(_rented);
        Headers.ContentType = JsonMediaType;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => stream.WriteAsync(_rented!.AsMemory(0, _length)).AsTask();

    protected override bool TryComputeLength(out long length)
    {
        length = _length;
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _rented is { } rented)
        {
            ArrayPool<byte>.Shared.Return(rented);
            _rented = null;
        }

        base.Dispose(disposing);
    }
}
