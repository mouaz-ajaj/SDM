using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace SDM.Infrastructure.Logging;

public static class LoggingBuilderExtensions
{
    /// <summary>
    /// Writes the log to a rolling file. A Windows executable has no console, so without
    /// this every line the application logs — including the reason it failed — is lost.
    /// </summary>
    public static ILoggingBuilder AddSdmFileLogging(this ILoggingBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        FileLogOptions options = configuration.GetSection(FileLogOptions.SectionName).Get<FileLogOptions>()
            ?? new FileLogOptions();

        FileLoggerProvider provider = new(options);
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider>(provider));
        builder.Services.AddSingleton(provider);

        return builder;
    }
}
