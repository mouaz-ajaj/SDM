namespace SDM.Application.Downloads;

/// <summary>
/// Where finished downloads are written. Implemented in the infrastructure layer so the
/// application never queries the operating system directly.
/// </summary>
public interface IDownloadFolder
{
    string GetPath();
}
