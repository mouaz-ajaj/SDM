using SDM.Core.Downloads;

namespace SDM.Core.Tests.Downloads;

public sealed class MediaTypeExtensionsTests
{
    [Theory]
    [InlineData("image/jpeg", ".jpg")]
    [InlineData("image/png", ".png")]
    [InlineData("image/webp", ".webp")]
    [InlineData("image/gif", ".gif")]
    [InlineData("video/mp4", ".mp4")]
    [InlineData("audio/mpeg", ".mp3")]
    [InlineData("application/pdf", ".pdf")]
    [InlineData("application/zip", ".zip")]
    [InlineData("text/html", ".html")]
    public void KnownTypes_GiveTheExtensionPeopleExpect(string mediaType, string expected) =>
        Assert.Equal(expected, MediaTypeExtensions.ForMediaType(mediaType));

    [Fact]
    public void CharsetParameter_IsIgnored() =>
        Assert.Equal(".html", MediaTypeExtensions.ForMediaType("text/html; charset=utf-8"));

    [Theory]
    [InlineData("image/svg+xml", ".svg")]
    [InlineData("audio/x-flac", ".flac")]
    public void SubtypeSuffixAndVendorPrefix_AreStripped(string mediaType, string expected) =>
        Assert.Equal(expected, MediaTypeExtensions.ForMediaType(mediaType));

    [Fact]
    public void OctetStream_SuggestsNothing()
    {
        // It is the type a server sends when it does not know either. Inventing an
        // extension from it would be a guess dressed up as knowledge.
        Assert.Null(MediaTypeExtensions.ForMediaType("application/octet-stream"));
    }

    [Theory]
    [InlineData("application/x-some-vendor-thing")]
    [InlineData("application/vnd.unheard-of")]
    [InlineData("nonsense")]
    [InlineData("image/")]
    [InlineData("")]
    [InlineData(null)]
    public void UnknownTypes_SuggestNothing(string? mediaType) =>
        Assert.Null(MediaTypeExtensions.ForMediaType(mediaType));

    [Fact]
    public void AnImplausibleSubtype_IsNotTurnedIntoAnExtension()
    {
        // The generic rule only applies where a subtype really is the extension. Anything
        // longer or stranger is left alone rather than pasted onto a file name.
        Assert.Null(MediaTypeExtensions.ForMediaType("video/vnd.dece.hd"));
    }
}
