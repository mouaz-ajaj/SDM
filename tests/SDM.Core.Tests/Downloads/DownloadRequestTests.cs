using SDM.Core.Downloads;

namespace SDM.Core.Tests.Downloads;

public sealed class DownloadRequestTests
{
    [Theory]
    [InlineData("https://example.test/file.bin")]
    [InlineData("http://example.test/file.bin")]
    public void Constructor_AcceptsAbsoluteHttpSources(string source)
    {
        DownloadRequest request = new(new Uri(source), "file.bin");

        Assert.Equal(source, request.Source.AbsoluteUri);
        Assert.Equal("file.bin", request.DestinationPath);
    }

    [Fact]
    public void Constructor_RejectsNonHttpSources()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new DownloadRequest(new Uri("file:///temporary/file.bin"), "file.bin"));

        Assert.Contains("HTTP or HTTPS", exception.Message, StringComparison.Ordinal);
    }
}
