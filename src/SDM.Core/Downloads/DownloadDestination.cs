namespace SDM.Core.Downloads;

/// <summary>
/// A folder and file name the user chose explicitly. Overrides both the category
/// sorting and the name the server suggested, because an explicit choice outranks
/// every guess the application could make.
/// </summary>
public sealed record DownloadDestination(string Directory, string FileName);
