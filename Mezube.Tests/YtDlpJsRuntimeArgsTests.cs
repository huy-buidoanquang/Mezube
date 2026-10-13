using Mezube.Bot;
using Mezube.Media;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mezube.Tests;

public sealed class YtDlpJsRuntimeArgsTests
{
    private static List<string> Args(string runtime, string path)
    {
        var processor = new YtDlpProcessor(
            new BotOptions { YtDlpJsRuntime = runtime, YtDlpJsRuntimePath = path },
            NullLogger<YtDlpProcessor>.Instance);
        return processor.PrependCommonArgs(["-J", "https://www.youtube.com/watch?v=dQw4w9WgXcQ"], playerClients: null);
    }

    [Theory]
    [InlineData("deno", "/usr/local/bin/deno", "deno:/usr/local/bin/deno")]
    [InlineData("deno", "", "deno")]
    [InlineData(" node ", " ", "node")]
    public void PrependCommonArgs_passes_configured_js_runtime(string runtime, string path, string expected)
    {
        var args = Args(runtime, path);

        var i = args.IndexOf("--js-runtimes");
        Assert.Equal(0, i);
        Assert.Equal(expected, args[i + 1]);
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", args[^1]);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("OFF")]
    [InlineData("")]
    public void PrependCommonArgs_omits_js_runtime_when_disabled(string runtime)
    {
        Assert.DoesNotContain("--js-runtimes", Args(runtime, "/usr/local/bin/deno"));
    }
}
