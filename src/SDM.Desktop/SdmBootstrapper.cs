using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SDM.Application;
using SDM.Database;
using SDM.Infrastructure;
using SDM.Infrastructure.Logging;

namespace SDM.Desktop;

public static class SdmBootstrapper
{
    public static ServiceProvider CreateServiceProvider(string? basePath = null)
    {
        IConfiguration configuration = BuildConfiguration(basePath);
        ServiceCollection services = new();

        services.AddSingleton(configuration);
        services.AddLogging(builder =>
        {
            builder.ClearProviders();

            // The console provider only reaches anyone when stdout has been redirected;
            // the file is what a user running the packaged application can actually read.
            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
            });
            builder.AddSdmFileLogging(configuration);
            builder.SetMinimumLevel(LogLevel.Information);
        });

        services.AddSdmApplication(configuration);
        services.AddSdmInfrastructure();
        services.AddSdmDatabase(configuration);
        services.AddSdmDesktop();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private static IConfiguration BuildConfiguration(string? basePath)
    {
        string contentRoot = basePath ?? AppContext.BaseDirectory;

        return new ConfigurationBuilder()
            .SetBasePath(contentRoot)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .Build();
    }
}
