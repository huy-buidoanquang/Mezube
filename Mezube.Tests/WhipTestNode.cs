using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Mezube.Tests;

internal sealed record WhipRequest(
    string Method,
    string Path,
    string? Authorization,
    string? ContentType,
    long? ContentLength,
    string Body);

internal sealed record WhipReply(int Status, string? Sdp = null, string? Location = null);

internal sealed class WhipTestNode : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<WhipRequest> _requests = new();

    private WhipTestNode(WebApplication app)
    {
        _app = app;
    }

    public Func<string, Task<WhipReply>> Reply { get; set; } = _ => Task.FromResult(new WhipReply(503));

    public IReadOnlyList<WhipRequest> Requests => _requests.ToArray();

    public Uri BaseUri { get; private set; } = null!;

    public Uri Url(string path) => new(BaseUri, path);

    public static async Task<WhipTestNode> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        var node = new WhipTestNode(app);
        app.MapPost("/{**path}", node.HandlePostAsync);
        app.MapDelete("/{**path}", node.HandleDelete);
        await app.StartAsync();
        node.BaseUri = new Uri(app.Urls.First());
        return node;
    }

    public static string CannedAnswer(string offerSdp, string candidate = "127.0.0.1 9")
    {
        var mid = Regex.Match(offerSdp, @"a=mid:(\S+)").Groups[1].Value;
        var payloadType = Regex.Match(offerSdp, @"a=rtpmap:(\d+) opus/48000", RegexOptions.IgnoreCase).Groups[1].Value;
        var fingerprint = string.Join(":", Enumerable.Range(0, 32).Select(i => (i * 7 % 256).ToString("X2")));
        return string.Join("\r\n",
        [
            "v=0",
            "o=- 1 2 IN IP4 127.0.0.1",
            "s=-",
            "t=0 0",
            "a=ice-lite",
            $"a=group:BUNDLE {mid}",
            $"m=audio 9 UDP/TLS/RTP/SAVPF {payloadType}",
            "c=IN IP4 0.0.0.0",
            $"a=mid:{mid}",
            "a=ice-ufrag:0a1b2c3d",
            "a=ice-pwd:0123456789abcdef01234567",
            $"a=fingerprint:sha-256 {fingerprint}",
            "a=setup:passive",
            "a=recvonly",
            "a=rtcp-mux",
            $"a=rtpmap:{payloadType} opus/48000/2",
            $"a=candidate:1 1 udp 2130706431 {candidate} typ host",
            "a=end-of-candidates",
            string.Empty,
        ]);
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private async Task HandlePostAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        Record(context, body);
        var reply = await Reply(body);
        context.Response.StatusCode = reply.Status;
        if (reply.Location is not null)
        {
            context.Response.Headers.Location = reply.Location;
        }

        if (reply.Sdp is not null)
        {
            context.Response.ContentType = "application/sdp";
            await context.Response.WriteAsync(reply.Sdp);
        }
    }

    private Task HandleDelete(HttpContext context)
    {
        Record(context, string.Empty);
        context.Response.StatusCode = StatusCodes.Status200OK;
        return Task.CompletedTask;
    }

    private void Record(HttpContext context, string body)
        => _requests.Enqueue(new WhipRequest(
            context.Request.Method,
            context.Request.Path.Value ?? string.Empty,
            context.Request.Headers.Authorization.ToString(),
            context.Request.ContentType,
            context.Request.ContentLength,
            body));
}
