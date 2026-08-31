using Microsoft.Extensions.DependencyInjection;

namespace SDM.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddSdmInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Concrete networking and operating-system services arrive in later stages.
        return services;
    }
}
