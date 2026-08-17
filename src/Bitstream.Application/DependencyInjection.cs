using Microsoft.Extensions.DependencyInjection;

namespace Bitstream.Application;

/// <summary>
/// Registration entry point for the application layer.
/// Each layer exposes exactly one of these; Program.cs calls them in order and is the only
/// place in the solution that knows all four layers exist.
/// </summary>
public static class DependencyInjection
{
    /// <summary>Registers application services. Implementations are added as modules are built.</summary>
    public static IServiceCollection AddBitstreamApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Scaffold stage: no implementations registered yet. Service interfaces live in
        // Bitstream.Application.Services and are registered here, never in the API project.
        return services;
    }
}
