namespace SDM.Core.Downloads;

/// <summary>
/// Decides what kind of file something is. The extension is trusted first because it is
/// what the user sees and what Windows opens the file with; the server's Content-Type is
/// the fallback for the many URLs that end in an opaque id with no extension at all.
/// </summary>
public static class FileCategories
{
    private static readonly Dictionary<string, FileCategory> ByExtension =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = FileCategory.Documents, [".doc"] = FileCategory.Documents,
            [".docx"] = FileCategory.Documents, [".xls"] = FileCategory.Documents,
            [".xlsx"] = FileCategory.Documents, [".ppt"] = FileCategory.Documents,
            [".pptx"] = FileCategory.Documents, [".txt"] = FileCategory.Documents,
            [".rtf"] = FileCategory.Documents, [".odt"] = FileCategory.Documents,
            [".ods"] = FileCategory.Documents, [".odp"] = FileCategory.Documents,
            [".epub"] = FileCategory.Documents, [".mobi"] = FileCategory.Documents,
            [".csv"] = FileCategory.Documents, [".md"] = FileCategory.Documents,

            [".zip"] = FileCategory.Compressed, [".rar"] = FileCategory.Compressed,
            [".7z"] = FileCategory.Compressed, [".tar"] = FileCategory.Compressed,
            [".gz"] = FileCategory.Compressed, [".bz2"] = FileCategory.Compressed,
            [".xz"] = FileCategory.Compressed, [".zst"] = FileCategory.Compressed,
            [".iso"] = FileCategory.Compressed, [".cab"] = FileCategory.Compressed,

            [".exe"] = FileCategory.Programs, [".msi"] = FileCategory.Programs,
            [".msix"] = FileCategory.Programs, [".appx"] = FileCategory.Programs,
            [".bat"] = FileCategory.Programs, [".cmd"] = FileCategory.Programs,
            [".ps1"] = FileCategory.Programs, [".sh"] = FileCategory.Programs,
            [".deb"] = FileCategory.Programs, [".rpm"] = FileCategory.Programs,
            [".dmg"] = FileCategory.Programs, [".pkg"] = FileCategory.Programs,
            [".apk"] = FileCategory.Programs,

            [".mp4"] = FileCategory.Video, [".mkv"] = FileCategory.Video,
            [".avi"] = FileCategory.Video, [".mov"] = FileCategory.Video,
            [".wmv"] = FileCategory.Video, [".flv"] = FileCategory.Video,
            [".webm"] = FileCategory.Video, [".m4v"] = FileCategory.Video,
            [".mpg"] = FileCategory.Video, [".mpeg"] = FileCategory.Video,
            [".ts"] = FileCategory.Video, [".m3u8"] = FileCategory.Video,

            [".mp3"] = FileCategory.Audio, [".wav"] = FileCategory.Audio,
            [".flac"] = FileCategory.Audio, [".aac"] = FileCategory.Audio,
            [".ogg"] = FileCategory.Audio, [".m4a"] = FileCategory.Audio,
            [".wma"] = FileCategory.Audio, [".opus"] = FileCategory.Audio,

            [".jpg"] = FileCategory.Images, [".jpeg"] = FileCategory.Images,
            [".png"] = FileCategory.Images, [".gif"] = FileCategory.Images,
            [".bmp"] = FileCategory.Images, [".webp"] = FileCategory.Images,
            [".svg"] = FileCategory.Images, [".tiff"] = FileCategory.Images,
            [".ico"] = FileCategory.Images, [".heic"] = FileCategory.Images,
        };

    private static readonly Dictionary<string, FileCategory> ByMediaType =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["application/pdf"] = FileCategory.Documents,
            ["application/msword"] = FileCategory.Documents,
            ["application/rtf"] = FileCategory.Documents,
            ["text/csv"] = FileCategory.Documents,
            ["text/plain"] = FileCategory.Documents,
            ["application/zip"] = FileCategory.Compressed,
            ["application/x-7z-compressed"] = FileCategory.Compressed,
            ["application/vnd.rar"] = FileCategory.Compressed,
            ["application/gzip"] = FileCategory.Compressed,
            ["application/x-tar"] = FileCategory.Compressed,
            ["application/x-iso9660-image"] = FileCategory.Compressed,
            ["application/vnd.microsoft.portable-executable"] = FileCategory.Programs,
            ["application/x-msdownload"] = FileCategory.Programs,
            ["application/vnd.android.package-archive"] = FileCategory.Programs,
        };

    /// <summary>Folder names, kept separate from the enum so they can be renamed freely.</summary>
    public static string FolderNameFor(FileCategory category) => category switch
    {
        FileCategory.Documents => "Documents",
        FileCategory.Compressed => "Compressed",
        FileCategory.Programs => "Programs",
        FileCategory.Video => "Video",
        FileCategory.Audio => "Audio",
        FileCategory.Images => "Images",
        _ => "Other",
    };

    public static FileCategory Resolve(string? fileName, string? mediaType = null)
    {
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            string extension = Path.GetExtension(fileName);

            if (extension.Length > 1 && ByExtension.TryGetValue(extension, out FileCategory byExtension))
            {
                return byExtension;
            }
        }

        return FromMediaType(mediaType);
    }

    private static FileCategory FromMediaType(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return FileCategory.Other;
        }

        // Trim any "; charset=..." the server appended.
        int parameters = mediaType.IndexOf(';', StringComparison.Ordinal);
        string media = (parameters < 0 ? mediaType : mediaType[..parameters]).Trim();

        if (ByMediaType.TryGetValue(media, out FileCategory known))
        {
            return known;
        }

        // The top-level type is a reliable last resort: video/anything is a video.
        int slash = media.IndexOf('/', StringComparison.Ordinal);
        string top = slash < 0 ? media : media[..slash];

        return top.ToLowerInvariant() switch
        {
            "video" => FileCategory.Video,
            "audio" => FileCategory.Audio,
            "image" => FileCategory.Images,
            "text" => FileCategory.Documents,
            _ => FileCategory.Other,
        };
    }
}
