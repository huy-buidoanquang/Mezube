using Mezube.Bot;
using Mezube.Streaming;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;

namespace Mezube.Tests;

public sealed class WhipSignalingTests
{
    [Fact]
    public async Task Busy_channel_posts_one_bearer_sdp_offer_and_fails_as_channel_busy()
    {
        await using var node = await WhipTestNode.StartAsync();
        node.Reply = _ => Task.FromResult(new WhipReply(409));
        using var http = new HttpClient();
        var session = NewSession(http, maxAttempts: 3);
        try
        {
            var failure = await Assert.ThrowsAsync<StreamPublishException>(() => session.StartAsync(
                _ => Task.FromResult(new StreamPublishGrant(node.Url("whip/42"), "t1")),
                CancellationToken.None));

            Assert.Equal(StreamFailureKind.ChannelBusy, failure.Kind);
            var post = Assert.Single(node.Requests);
            Assert.Equal("POST", post.Method);
            Assert.Equal("/whip/42", post.Path);
            Assert.Equal("Bearer t1", post.Authorization);
            Assert.Equal("application/sdp", post.ContentType);
            Assert.Equal(Encoding.UTF8.GetByteCount(post.Body), post.ContentLength);
            Assert.Contains("a=setup:actpass", post.Body);
            Assert.DoesNotContain("m=video", post.Body);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task Busy_node_is_retried_with_a_fresh_grant_until_the_attempt_limit()
    {
        await using var node = await WhipTestNode.StartAsync();
        node.Reply = _ => Task.FromResult(new WhipReply(503));
        using var http = new HttpClient();
        var session = NewSession(http, maxAttempts: 2);
        var grants = 0;
        try
        {
            var failure = await Assert.ThrowsAsync<StreamPublishException>(() => session.StartAsync(
                _ => Task.FromResult(new StreamPublishGrant(node.Url("whip/42"), $"t{Interlocked.Increment(ref grants)}")),
                CancellationToken.None));

            Assert.Equal(StreamFailureKind.Unavailable, failure.Kind);
            Assert.Equal(["Bearer t1", "Bearer t2", "Bearer t3"], node.Requests.Select(request => request.Authorization));
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task Denied_grant_fails_without_posting()
    {
        await using var node = await WhipTestNode.StartAsync();
        using var http = new HttpClient();
        var session = NewSession(http, maxAttempts: 3);
        try
        {
            var failure = await Assert.ThrowsAsync<StreamPublishException>(() => session.StartAsync(
                _ => Task.FromException<StreamPublishGrant>(new StreamPublishException(StreamFailureKind.Denied, "not a member")),
                CancellationToken.None));

            Assert.Equal(StreamFailureKind.Denied, failure.Kind);
            Assert.Empty(node.Requests);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task Stop_deletes_the_resource_resolved_against_the_whip_url()
    {
        await using var node = await WhipTestNode.StartAsync();
        node.Reply = offer => Task.FromResult(new WhipReply(201, WhipTestNode.CannedAnswer(offer), "/resource/1.2.abc"));
        using var http = new HttpClient();
        var session = NewSession(http, maxAttempts: 0);

        await Record.ExceptionAsync(() => session.StartAsync(
            _ => Task.FromResult(new StreamPublishGrant(node.Url("prefix/whip/42"), "t1")),
            CancellationToken.None));
        await session.StopAsync(CancellationToken.None);

        Assert.Equal(["POST /prefix/whip/42", "DELETE /resource/1.2.abc"], node.Requests.Select(request => $"{request.Method} {request.Path}"));
    }

    private static StreamPublisherSession NewSession(HttpClient http, int maxAttempts)
        => new(
            new BotOptions
            {
                StreamConnectTimeoutMs = 1000,
                StreamReconnectMaxAttempts = maxAttempts,
                StreamReconnectBackoffMs = 1,
                StreamReconnectMaxBackoffMs = 1,
                StreamReconnectJitterMs = 0,
            },
            http,
            NullLogger.Instance,
            42);
}
