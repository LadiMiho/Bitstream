using System.Reflection;
using Bitstream.Application.Abstractions.Integration;
using Xunit;

namespace Bitstream.ArchitectureTests;

/// <summary>
/// Executable form of TR-ARC-01 and TR-ARC-02: business logic must not call external systems
/// directly, and every integration must go through a port implemented in the integration layer.
/// <para>
/// The assertions read compiled assembly references, which the C# compiler emits only for
/// assemblies a project actually uses. A layering breach therefore fails the build rather
/// than waiting for a reviewer to notice it.
/// </para>
/// </summary>
public sealed class LayeringTests
{
    private static readonly Assembly Domain = typeof(Domain.Entities.Isp).Assembly;
    private static readonly Assembly Application = typeof(ICrmGateway).Assembly;
    private static readonly Assembly Persistence = typeof(Infrastructure.Persistence.BitstreamDbContext).Assembly;
    private static readonly Assembly Integration = typeof(Infrastructure.Integration.Crm.CrmHttpGateway).Assembly;

    private static string[] ReferencedAssemblyNames(Assembly assembly) =>
        [.. assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty)];

    [Fact]
    public void Domain_references_no_other_solution_assembly()
    {
        var references = ReferencedAssemblyNames(Domain);

        Assert.DoesNotContain(references, name => name.StartsWith("Bitstream.", StringComparison.Ordinal));
    }

    [Fact]
    public void Domain_references_no_infrastructure_technology()
    {
        var references = ReferencedAssemblyNames(Domain);

        Assert.DoesNotContain(references, name =>
            name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
            name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) ||
            name.Equals("System.Net.Http", StringComparison.Ordinal));
    }

    [Fact]
    public void Application_does_not_reference_infrastructure_projects()
    {
        var references = ReferencedAssemblyNames(Application);

        Assert.DoesNotContain(references, name =>
            name.StartsWith("Bitstream.Infrastructure", StringComparison.Ordinal) ||
            name.StartsWith("Bitstream.Api", StringComparison.Ordinal));
    }

    /// <summary>
    /// TR-ARC-01. An HTTP client, a mail client or a database driver inside the application
    /// layer means business logic is talking to an external system without an adapter.
    /// </summary>
    [Fact]
    public void Application_does_not_reference_external_transport_or_data_access()
    {
        var references = ReferencedAssemblyNames(Application);

        Assert.DoesNotContain(references, name =>
            name.Equals("System.Net.Http", StringComparison.Ordinal) ||
            name.Equals("System.Data.SqlClient", StringComparison.Ordinal) ||
            name.Equals("Microsoft.Data.SqlClient", StringComparison.Ordinal) ||
            name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
    }

    [Fact]
    public void Persistence_and_integration_do_not_reference_each_other()
    {
        Assert.DoesNotContain(
            ReferencedAssemblyNames(Persistence),
            name => name.StartsWith("Bitstream.Infrastructure.Integration", StringComparison.Ordinal));

        Assert.DoesNotContain(
            ReferencedAssemblyNames(Integration),
            name => name.StartsWith("Bitstream.Infrastructure.Persistence", StringComparison.Ordinal));
    }

    /// <summary>TR-ARC-02: every port implementation lives in the integration layer.</summary>
    [Theory]
    [InlineData(typeof(ICrmGateway))]
    [InlineData(typeof(IBiGateway))]
    [InlineData(typeof(ISapGateway))]
    [InlineData(typeof(IEmailGateway))]
    public void Every_integration_port_is_implemented_only_in_the_integration_layer(Type port)
    {
        var implementations = new[] { Domain, Application, Persistence, Integration }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsInterface: false, IsAbstract: false } && port.IsAssignableFrom(type))
            .ToList();

        Assert.NotEmpty(implementations);
        Assert.All(implementations, type => Assert.Equal(Integration, type.Assembly));
    }
}
