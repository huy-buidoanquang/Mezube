using Mezube.Playback;
using System.Text;

namespace Mezube.Tests;

public sealed class SfuMeetTokenTests
{
    private static readonly string Jwt = string.Join(
        '.',
        Base64Url("""{"alg":"HS256","typ":"JWT"}"""),
        Base64Url("""{"iss":"mezon","room":"1840602337795543040","identity":"1840602337795543041","exp":1791446400,"metadata":"{\"name\":\"Mezube\",\"avatar\":\"\"}"}"""),
        "dGVzdC1zaWduYXR1cmUtbm90LWEtcmVhbC1obWFjLXZhbHVl");

    [Fact]
    public void ExtractMeetJwt_returns_raw_jwt_unchanged()
        => Assert.Equal(Jwt, SfuStreamingChannelSink.ExtractMeetJwt(Jwt));

    [Fact]
    public void ExtractMeetJwt_strips_protobuf_framing_decoded_as_utf8()
    {
        // What Mezon.Net.Sdk (through 1.6.2) hands back for a packed GenerateMeetTokenResponse.
        var decodedBySdk = Encoding.UTF8.GetString(PackedResponse(Jwt));

        Assert.NotEqual(Jwt, decodedBySdk);
        Assert.Equal(Jwt, SfuStreamingChannelSink.ExtractMeetJwt(decodedBySdk));
    }

    [Fact]
    public void ExtractMeetJwt_ignores_trailing_url_field()
    {
        var decodedBySdk = Encoding.UTF8.GetString(PackedResponse(Jwt, "wss://sfu.example.test/ws"));

        Assert.Equal(Jwt, SfuStreamingChannelSink.ExtractMeetJwt(decodedBySdk));
    }

    [Theory]
    [InlineData("not-a-token")]
    [InlineData("eyJabc.def")]
    public void ExtractMeetJwt_rejects_values_without_a_jwt(string token)
        => Assert.Throws<InvalidOperationException>(() => SfuStreamingChannelSink.ExtractMeetJwt(token));

    private static byte[] PackedResponse(string token, string? url = null)
    {
        using var stream = new MemoryStream();
        WriteStringField(stream, 1, token);
        if (url is not null)
        {
            WriteStringField(stream, 2, url);
        }

        return stream.ToArray();
    }

    private static void WriteStringField(Stream stream, int field, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.WriteByte((byte)((field << 3) | 2));
        var length = (uint)bytes.Length;
        while (length >= 0x80)
        {
            stream.WriteByte((byte)(length | 0x80));
            length >>= 7;
        }

        stream.WriteByte((byte)length);
        stream.Write(bytes);
    }

    private static string Base64Url(string json)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
