using SDM.Core.Downloads;

namespace SDM.Core.Tests.Downloads;

public sealed class SafeFileNameTests
{
    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData(@"..\..\Windows\System32\evil.dll", "evil.dll")]
    [InlineData(@"C:\Windows\System32\evil.dll", "evil.dll")]
    [InlineData("\"quoted name.zip\"", "quoted name.zip")]
    [InlineData("with:invalid*chars?.txt", "with_invalid_chars_.txt")]
    [InlineData("trailing dots... ", "trailing dots")]
    public void Sanitize_ReducesUntrustedNamesToASingleSafeSegment(string candidate, string expected)
    {
        Assert.Equal(expected, SafeFileName.Sanitize(candidate));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("/")]
    public void Sanitize_FallsBackWhenNothingUsableRemains(string? candidate)
    {
        Assert.Equal(SafeFileName.Fallback, SafeFileName.Sanitize(candidate));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("COM1.bin")]
    public void Sanitize_EscapesReservedWindowsDeviceNames(string candidate)
    {
        string sanitized = SafeFileName.Sanitize(candidate);

        Assert.StartsWith("_", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_TruncatesLongNamesButKeepsTheExtension()
    {
        string sanitized = SafeFileName.Sanitize(new string('a', 400) + ".tar.gz");

        Assert.True(sanitized.Length <= 150, $"Name was {sanitized.Length} characters.");
        Assert.EndsWith(".gz", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void FromUri_UsesTheLastSegmentAndIgnoresTheQuery()
    {
        Uri source = new("https://example.test/downloads/quarterly%20report.pdf?token=abc123");

        Assert.Equal("quarterly report.pdf", SafeFileName.FromUri(source));
    }

    [Fact]
    public void FromUri_FallsBackWhenTheUrlHasNoFileSegment()
    {
        Assert.Equal(SafeFileName.Fallback, SafeFileName.FromUri(new Uri("https://example.test/")));
    }

    [Fact]
    public void Resolve_PrefersTheServerSuggestionOverTheUrl()
    {
        Uri source = new("https://example.test/opaque-id");

        Assert.Equal("invoice.pdf", SafeFileName.Resolve("invoice.pdf", source));
        Assert.Equal("opaque-id", SafeFileName.Resolve(null, source));
        Assert.Equal("opaque-id", SafeFileName.Resolve("   ", source));
    }
}
