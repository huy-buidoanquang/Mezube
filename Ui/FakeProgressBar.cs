using Mezon.Net.Client;
using Mezon.Net.Sdk.Entities;
using Microsoft.Extensions.Logging;

namespace Mezube.Ui;

/// <summary>
/// Edits a message once per second with content from <paramref name="buildContent"/>.
/// Used for the live progress bar on <c>!np</c> replies.
/// </summary>
public sealed class FakeProgressBar : IAsyncDisposable
{
    private readonly TextChannel _channel;
    private readonly long _messageId;
    private readonly uint? _createTimeSeconds;
    private readonly Func<MessageContent?> _buildContent;
    private readonly ILogger? _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    private FakeProgressBar(
        TextChannel channel,
        long messageId,
        uint? createTimeSeconds,
        Func<MessageContent?> buildContent,
        ILogger? logger)
    {
        _channel = channel;
        _messageId = messageId;
        _createTimeSeconds = createTimeSeconds;
        _buildContent = buildContent;
        _logger = logger;
        _loop = RunAsync(_cts.Token);
    }

    public static FakeProgressBar Start(
        TextChannel channel,
        long messageId,
        Func<MessageContent?> buildContent,
        ILogger? logger = null,
        uint? createTimeSeconds = null)
        => new(channel, messageId, createTimeSeconds, buildContent, logger);

    public async Task StopAsync()
    {
        _cts.Cancel();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            MessageContent? content;
            try
            {
                content = _buildContent();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Progress content build failed for message {MessageId}", _messageId);
                break;
            }

            if (content is null)
            {
                break;
            }

            try
            {
                await _channel.UpdateMessageAsync(
                        _messageId,
                        content,
                        hideEdited: true,
                        createTimeSeconds: _createTimeSeconds)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Progress update failed for message {MessageId}", _messageId);
            }
        }
    }
}
