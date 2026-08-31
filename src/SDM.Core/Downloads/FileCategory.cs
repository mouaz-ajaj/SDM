namespace SDM.Core.Downloads;

/// <summary>
/// The bucket a downloaded file belongs to. Used to pick a sub-folder and, later, an
/// icon — so the list can be read at a glance instead of parsed name by name.
/// </summary>
public enum FileCategory
{
    Other,
    Documents,
    Compressed,
    Programs,
    Video,
    Audio,
    Images,
}
