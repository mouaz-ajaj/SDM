using SDM.Core.Downloads;

namespace SDM.Core.Tests.Downloads;

public sealed class FileCategoriesTests
{
    [Theory]
    [InlineData("report.pdf", FileCategory.Documents)]
    [InlineData("notes.TXT", FileCategory.Documents)]
    [InlineData("archive.zip", FileCategory.Compressed)]
    [InlineData("ubuntu-24.04.1-desktop-amd64.iso", FileCategory.Compressed)]
    [InlineData("Git-2.47.1-64-bit.exe", FileCategory.Programs)]
    [InlineData("PowerShell-7.4.6-win-x64.msi", FileCategory.Programs)]
    [InlineData("holiday.mp4", FileCategory.Video)]
    [InlineData("song.flac", FileCategory.Audio)]
    [InlineData("logo.png", FileCategory.Images)]
    [InlineData("1GB.bin", FileCategory.Other)]
    public void Resolve_ClassifiesByExtension(string fileName, FileCategory expected)
    {
        Assert.Equal(expected, FileCategories.Resolve(fileName));
    }

    [Theory]
    [InlineData("video/mp4", FileCategory.Video)]
    [InlineData("audio/mpeg", FileCategory.Audio)]
    [InlineData("image/avif", FileCategory.Images)]
    [InlineData("application/pdf", FileCategory.Documents)]
    [InlineData("application/zip", FileCategory.Compressed)]
    [InlineData("application/octet-stream", FileCategory.Other)]
    public void Resolve_FallsBackToTheServersTypeWhenThereIsNoExtension(
        string mediaType, FileCategory expected)
    {
        // Plenty of download URLs end in an opaque id, so the header is all there is.
        Assert.Equal(expected, FileCategories.Resolve("download", mediaType));
    }

    [Fact]
    public void Resolve_IgnoresContentTypeParameters()
    {
        Assert.Equal(FileCategory.Documents, FileCategories.Resolve("readme", "text/plain; charset=utf-8"));
    }

    [Fact]
    public void Resolve_PrefersTheExtensionOverTheServersType()
    {
        // Servers label everything application/octet-stream; the extension is what the
        // user sees and what Windows opens the file with.
        Assert.Equal(
            FileCategory.Video,
            FileCategories.Resolve("holiday.mp4", "application/octet-stream"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("noextension")]
    public void Resolve_FallsBackToOtherWhenNothingIsKnown(string? fileName)
    {
        Assert.Equal(FileCategory.Other, FileCategories.Resolve(fileName));
    }

    [Fact]
    public void Resolve_DoesNotTreatAVersionNumberAsAnExtension()
    {
        // "node-v22.11.0-x64" has a dot in it but no real extension.
        Assert.Equal(FileCategory.Other, FileCategories.Resolve("node-v22.11.0-x64"));
    }

    [Fact]
    public void FolderNameFor_NamesEveryCategory()
    {
        foreach (FileCategory category in Enum.GetValues<FileCategory>())
        {
            Assert.False(string.IsNullOrWhiteSpace(FileCategories.FolderNameFor(category)));
        }
    }
}
