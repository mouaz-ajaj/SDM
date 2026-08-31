using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SDM.Application.ApplicationInfo;

namespace SDM.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        ServiceProvider? services = null;

        try
        {
            services = SdmBootstrapper.CreateServiceProvider();
            ILogger logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("SDM.Startup");
            IApplicationInfoService appInfo = services.GetRequiredService<IApplicationInfoService>();

            logger.LogInformation("Starting {ApplicationName}.", appInfo.FullName);
            logger.LogInformation("Application version: {ApplicationVersion}.", appInfo.Version);
            logger.LogInformation("Dependencies initialized successfully.");

            BuildAvaloniaApp(services).StartWithClassicDesktopLifetime(args);

            logger.LogInformation("Application shutdown completed.");
            return 0;
        }
        catch (Exception exception)
        {
            ILogger? logger = services?.GetService<ILoggerFactory>()?.CreateLogger("SDM.Startup");
            logger?.LogCritical(exception, "Unexpected startup failure.");
            Console.Error.WriteLine($"SDM failed to start: {exception.Message}");
            return 1;
        }
        finally
        {
            services?.Dispose();
        }
    }

    private static AppBuilder BuildAvaloniaApp(IServiceProvider services)
    {
        return AppBuilder.Configure(() => new App(services))
            .UsePlatformDetect()
            .LogToTrace();
    }
}
