using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SDM.Application.Downloads;
using SDM.Application.Integration;
using SDM.Application.Settings;
using SDM.Core.Downloads;
using SDM.Infrastructure.Downloads;
using SDM.Infrastructure.Integration;
using SDM.Infrastructure.Settings;

namespace SDM.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddSdmInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(HttpDownloadEngine.HttpClientName, client =>
            {
                // HttpClient.Timeout spans the whole response including the body, so any
                // finite value would abort long transfers. Callers cancel instead.
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.UserAgent.ParseAdd("SDM/0.1");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(30),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),

                // A download manager stores bytes exactly as served. Transparent
                // decompression would desync the file from its advertised length.
                AutomaticDecompression = DecompressionMethods.None,
            });

        services.AddSingleton<IDownloadEngine, HttpDownloadEngine>();
        services.AddSingleton<IDownloadFolder, DefaultDownloadFolder>();
        services.AddSingleton<IUserSettingsStore, JsonUserSettingsStore>();
        services.AddSingleton<IBrowserBridge, NamedPipeBrowserBridge>();
        return services;
    }
}
