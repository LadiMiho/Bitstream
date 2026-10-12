using Microsoft.Extensions.Logging.Abstractions;

namespace Bitstream.Hosting.Logging;

public static class FileLoggingExtensions
{
    /// <summary>
    /// Adds the <c>Logging:File</c> text log (see <see cref="FileLoggerOptions"/>). Called right
    /// after the builder is created, so start-up failures are written too.
    /// </summary>
    /// <param name="builder">The host being configured.</param>
    /// <param name="defaultPrefix">File name start when <c>Logging:File:FileNamePrefix</c> is not set, e.g. <c>bitstream-web</c>.</param>
    public static WebApplicationBuilder AddBitstreamFileLogging(this WebApplicationBuilder builder, string defaultPrefix)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Resolved from the built container (not read here) so configuration added later —
        // environment variables, a test host's overrides — is what decides. Registered as a
        // service so the container disposes it at shutdown, flushing the last entries.
        builder.Services.AddSingleton<ILoggerProvider>(services =>
        {
            var options = services.GetRequiredService<IConfiguration>().GetSection(FileLoggerOptions.SectionName).Get<FileLoggerOptions>()
                ?? new FileLoggerOptions();

            if (!options.Enabled)
            {
                return NullLoggerProvider.Instance;
            }

            options.FileNamePrefix ??= defaultPrefix;

            return new FileLoggerProvider(options, services.GetRequiredService<IHostEnvironment>().ContentRootPath);
        });

        return builder;
    }
}
