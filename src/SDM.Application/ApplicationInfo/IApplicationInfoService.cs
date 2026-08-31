namespace SDM.Application.ApplicationInfo;

public interface IApplicationInfoService
{
    string Name { get; }

    string FullName { get; }

    string Version { get; }
}
