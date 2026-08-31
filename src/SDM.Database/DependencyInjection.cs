using Microsoft.Extensions.DependencyInjection;

namespace SDM.Database;

public static class DependencyInjection
{
    public static IServiceCollection AddSdmDatabase(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Persistence is intentionally deferred; this method reserves its composition boundary.
        return services;
    }
}
