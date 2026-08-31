using Microsoft.Extensions.Options;
using SDM.Application.Downloads;

namespace SDM.Application.Tests.Downloads;

public sealed class CategoryDownloadLayoutTests
{
    private const string Root = @"C:\Users\M3aZ\Downloads";

    [Theory]
    [InlineData("report.pdf", null, "Documents")]
    [InlineData("archive.zip", null, "Compressed")]
    [InlineData("setup.exe", null, "Programs")]
    [InlineData("holiday.mp4", null, "Video")]
    [InlineData("song.mp3", null, "Audio")]
    [InlineData("logo.png", null, "Images")]
    [InlineData("opaque-id", "video/mp4", "Video")]
    public void ResolveDirectory_SortsIntoACategoryFolder(
        string fileName, string? mediaType, string expectedFolder)
    {
        CategoryDownloadLayout layout = Create(organize: true);

        Assert.Equal(
            Path.Combine(Root, expectedFolder),
            layout.ResolveDirectory(Root, fileName, mediaType));
    }

    [Fact]
    public void ResolveDirectory_LeavesUnrecognisedFilesAtTheTop()
    {
        // An "Other" folder nobody opens is worse than no folder at all.
        CategoryDownloadLayout layout = Create(organize: true);

        Assert.Equal(Root, layout.ResolveDirectory(Root, "1GB.bin", null));
    }

    [Fact]
    public void ResolveDirectory_DoesNothingWhenSortingIsOff()
    {
        CategoryDownloadLayout layout = Create(organize: false);

        Assert.Equal(Root, layout.ResolveDirectory(Root, "report.pdf", "application/pdf"));
    }

    [Fact]
    public void ResolveDirectory_NeverLeavesTheDownloadFolder()
    {
        CategoryDownloadLayout layout = Create(organize: true);

        string resolved = layout.ResolveDirectory(Root, "report.pdf", null);

        Assert.StartsWith(Root, resolved, StringComparison.OrdinalIgnoreCase);
    }

    private static CategoryDownloadLayout Create(bool organize) =>
        new(new TestOptions<DownloadOptions>(new DownloadOptions { OrganizeIntoCategoryFolders = organize }));
}
