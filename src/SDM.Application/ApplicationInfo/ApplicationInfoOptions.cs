namespace SDM.Application.ApplicationInfo;

public sealed class ApplicationInfoOptions
{
    public const string SectionName = "Application";

    public string Name { get; init; } = "SDM";

    public string FullName { get; init; } = "Speed Download Manager";
}
