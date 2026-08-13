using Mezube.Media;

namespace Mezube.Tests;

public sealed class DownloadedMediaFilesTests
{
    [Theory]
    [InlineData("mezube_abc.webm.part", true)]
    [InlineData("mezube_abc.part", true)]
    [InlineData("mezube_abc.ytdl", true)]
    [InlineData("mezube_abc.tmp", true)]
    [InlineData("mezube_abc.info.json", true)]
    [InlineData("mezube_abc.webm", false)]
    [InlineData("mezube_abc.m4a", false)]
    [InlineData("mezube_abc.opus", false)]
    public void IsJunkName(string name, bool expected)
        => Assert.Equal(expected, DownloadedMediaFiles.IsJunkName(name));

    [Fact]
    public void FindCompleted_skips_part_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mezube-dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var prefix = "mezube_work";
            File.WriteAllText(Path.Combine(dir, prefix + ".webm.part"), "partial");
            File.WriteAllText(Path.Combine(dir, prefix + ".ytdl"), "tmp");
            Assert.Null(DownloadedMediaFiles.FindCompleted(dir, prefix));

            File.WriteAllText(Path.Combine(dir, prefix + ".webm"), "ok");
            var found = DownloadedMediaFiles.FindCompleted(dir, prefix);
            Assert.NotNull(found);
            Assert.EndsWith(".webm", found, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
            }
        }
    }
}
