using System.Reflection;
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

    /// <summary>
    /// The version of the running application rather than of this assembly. The two only
    /// agree while every project happens to share one version number.
    /// </summary>
    public string Version
    {
        get
        {
            Assembly assembly = Assembly.GetEntryAssembly() ?? typeof(ApplicationInfoService).Assembly;

            string? informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            if (string.IsNullOrWhiteSpace(informational))
            {
                return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            }

            // Source Link appends "+<commit sha>", which is noise in a window chrome.
            int metadata = informational.IndexOf('+', StringComparison.Ordinal);
            return metadata < 0 ? informational : informational[..metadata];
        }
    }
}
