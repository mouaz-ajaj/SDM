using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SDM.Application.ApplicationInfo;

namespace SDM.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddSdmApplication(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ApplicationInfoOptions>()
            .Bind(configuration.GetSection(ApplicationInfoOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.Name), "Application name is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.FullName), "Application full name is required.")
            .ValidateOnStart();

        services.AddSingleton<IApplicationInfoService, ApplicationInfoService>();
        return services;
    }
}
