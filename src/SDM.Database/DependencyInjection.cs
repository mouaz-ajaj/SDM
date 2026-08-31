using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SDM.Application.Downloads;

namespace SDM.Database;

public static class DependencyInjection
{
    public static IServiceCollection AddSdmDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DownloadStorageOptions>()
            .Bind(configuration.GetSection(DownloadStorageOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.FileName),
                "Storage:FileName is required.")
            .ValidateOnStart();

        services.AddSingleton<IDownloadRepository, SqliteDownloadRepository>();
        return services;
    }
}
