using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Bitstream.Infrastructure.Persistence.Identity;

/// <summary>
/// Design-time-only factory so <c>dotnet ef migrations</c>/<c>dotnet ef database update</c> can
/// build a <see cref="BitstreamIdentityDbContext"/> without the full DI container (this project
/// has no parameterless host to run at design time). Never used at application run time — the
/// real context is registered in <see cref="DependencyInjection.AddBitstreamPersistence"/> with
/// its options resolved from <c>appsettings</c>/environment variables the normal way.
/// <para>
/// Connection string source, in order: the <c>BITSTREAM_ConnectionStrings__BitstreamDb</c>
/// environment variable (matching the <c>BITSTREAM_</c> prefix the real host binds
/// configuration from, and <see cref="DatabaseOptions"/>'s default
/// <c>ConnectionStringName</c>, <c>"BitstreamDb"</c>), then a local-development fallback
/// pointing at <c>(localdb)\MSSQLLocalDB</c> — good enough to generate a migration against,
/// never used to actually reach a shared database.
/// </para>
/// </summary>
public sealed class BitstreamIdentityDbContextFactory : IDesignTimeDbContextFactory<BitstreamIdentityDbContext>
{
    public BitstreamIdentityDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("BITSTREAM_ConnectionStrings__BitstreamDb") ??
            "Server=(localdb)\\MSSQLLocalDB;Database=Bitstream;Trusted_Connection=True;TrustServerCertificate=True;";

        var optionsBuilder = new DbContextOptionsBuilder<BitstreamIdentityDbContext>();
        optionsBuilder.UseSqlServer(connectionString);

        // Design-time-only, ephemeral key ring: `dotnet ef migrations`/`database update` never
        // reads or writes an actual UserTokens.Value, so what backs this protector doesn't matter.
        var dataProtectionProvider = DataProtectionProvider.Create("Bitstream.DesignTime");

        return new BitstreamIdentityDbContext(optionsBuilder.Options, dataProtectionProvider);
    }
}
