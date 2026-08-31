using Microsoft.Extensions.Options;

namespace SDM.Application.ApplicationInfo;

public sealed class ApplicationInfoService : IApplicationInfoService
{
    private readonly ApplicationInfoOptions _options;

    public ApplicationInfoService(IOptions<ApplicationInfoOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public string Name => _options.Name;

    public string FullName => _options.FullName;

    public string Version => typeof(ApplicationInfoService).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
}
