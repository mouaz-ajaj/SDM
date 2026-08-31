using SDM.Core.Downloads;

namespace SDM.Core.Tests.Downloads;

public sealed class DownloadRequestTests
{
    [Theory]
    [InlineData("https://example.test/file.bin")]
    [InlineData("http://example.test/file.bin")]
    public void Constructor_AcceptsAbsoluteHttpSources(string source)
    {
        DownloadRequest request = new(new Uri(source), Path.GetTempPath());

        Assert.Equal(source, request.Source.AbsoluteUri);
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()), request.DestinationDirectory);
        Assert.Null(request.PreferredFileName);
    }

    [Fact]
    public void Constructor_RejectsNonHttpSources()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new DownloadRequest(new Uri("file:///temporary/file.bin"), Path.GetTempPath()));

        Assert.Contains("HTTP or HTTPS", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_RejectsABlankDestinationDirectory()
    {
        Assert.Throws<ArgumentException>(
            () => new DownloadRequest(new Uri("https://example.test/file.bin"), "   "));
    }

    [Fact]
    public void Constructor_SanitizesAPreferredFileName()
    {
        DownloadRequest request = new(
            new Uri("https://example.test/file.bin"),
            Path.GetTempPath(),
            "../../escaped.bin");

        Assert.Equal("escaped.bin", request.PreferredFileName);
    }
}
