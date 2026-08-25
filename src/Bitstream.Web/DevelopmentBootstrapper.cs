using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using Bitstream.Application.Abstractions.Security;
using Bitstream.Application.Identity.Entities;
using Bitstream.Domain.Enums;
using Bitstream.Infrastructure.Persistence;
using Bitstream.Infrastructure.Persistence.Identity;
using Microsoft.EntityFrameworkCore;

namespace Bitstream.Web;

/// <summary>
/// Development-only convenience: applies <c>db/mssql</c> and seeds a local administrator, so a
/// fresh clone reaches a usable UI with <c>dotnet run</c> and nothing else.
/// <para>
/// <b>This can never run outside Development.</b> Two independent guards enforce that — the
/// environment must be Development, and <c>Database:DevelopmentAutoMigrate</c> must be true —
/// and the environment check is the one no configuration value can override. Applying DDL at
/// start-up and inserting a known-password account are both things that must never happen by
/// accident on UAT or production, where <c>db/Deploy-Database.ps1</c> is the only supported
/// path (TR-ARC-08) and accounts are created through the administration screens.
/// </para>
/// <para>
/// The scripts under <c>db/mssql</c> are hand-written and idempotent by design (ADR-0002), so
/// replaying them is safe; this applies them in numeric order and stamps
/// <c>ops.SchemaVersion</c> exactly as the PowerShell deployer does, which is what
/// <c>SchemaVersionGuard</c> then checks.
/// </para>
/// </summary>
public static class DevelopmentBootstrapper
{
    /// <summary>Configuration flag, required in addition to the environment being Development.</summary>
    public const string EnabledKey = "Database:DevelopmentAutoMigrate";

    /// <summary>
    /// Optional login for <c>0008_permissions.sql</c>, which is the one script written against
    /// a service account rather than the schema. Leave it unset locally: a developer normally
    /// connects as an administrator, for whom <c>CREATE USER ... FOR LOGIN</c> fails because
    /// that login is already mapped to <c>dbo</c>.
    /// </summary>
    public const string AppUserKey = "Database:DevelopmentAppUser";

    /// <summary>The sqlcmd variable <c>0008_permissions.sql</c> expects to be given.</summary>
    private const string AppUserVariable = "$(AppUser)";

    /// <summary>Applies pending schema scripts and seeds the development administrator. A no-op outside Development, or when the flag is off.</summary>
    public static async Task RunDevelopmentBootstrapAsync(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Not configurable: no setting makes this run outside Development.
        if (!app.Environment.IsDevelopment() || !app.Configuration.GetValue<bool>(EnabledKey))
        {
            return;
        }

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DevelopmentBootstrapper));

        await using var scope = app.Services.CreateAsyncScope();

        try
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            var identityDbContext = scope.ServiceProvider.GetRequiredService<BitstreamIdentityDbContext>();

            // Must run first: db/mssql/0014 (applied below, as part of the schema-script pass)
            // re-points several hand-written tables' foreign keys at dbo.Users/Roles,
            // and 0015 seeds roles into dbo.Roles — both need this migration's tables to
            // already exist.
            await identityDbContext.Database.MigrateAsync().ConfigureAwait(false);
            logger.LogInformation("Development bootstrap: identity schema migrated.");

            await ApplySchemaScriptsAsync(dbContext, app.Environment.ContentRootPath, app.Configuration[AppUserKey], logger).ConfigureAwait(false);
            await SeedAdministratorAsync(dbContext, scope.ServiceProvider, app.Configuration, logger).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // A developer convenience must never be the reason the app will not start: log it
            // and let the host come up, where SchemaVersionGuard reports the real schema state.
            logger.LogError(
                exception,
                "Development bootstrap failed. The application will still start; run db/Deploy-Database.ps1 by hand to see the underlying error.");
        }
    }

    /// <param name="dbContext">Supplies the connection; the scripts are applied over ADO.NET, not EF.</param>
    /// <param name="contentRootPath">Where the walk up to <c>db/mssql</c> starts.</param>
    /// <param name="appUser">Value for the <c>$(AppUser)</c> sqlcmd variable, or null to skip the script that needs it.</param>
    /// <param name="logger">Reports each applied script, and each deliberately skipped one.</param>
    private static async Task ApplySchemaScriptsAsync(
        BitstreamDbContext dbContext,
        string contentRootPath,
        string? appUser,
        ILogger logger)
    {
        var scriptDirectory = FindScriptDirectory(contentRootPath);

        if (scriptDirectory is null)
        {
            logger.LogWarning("Development bootstrap: db/mssql not found from {ContentRoot}; skipping schema apply.", contentRootPath);
            return;
        }

        var connection = dbContext.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync().ConfigureAwait(false);
        }

        var scripts = Directory.GetFiles(scriptDirectory, "*.sql")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();

        foreach (var path in scripts)
        {
            var sql = await File.ReadAllTextAsync(path).ConfigureAwait(false);

            // sqlcmd expands $(AppUser); ADO.NET does not, and the script raises rather than
            // grant rights to a name it was never given. Substitute it, or skip the script —
            // running it verbatim would abort the loop and leave later scripts unapplied.
            if (sql.Contains(AppUserVariable, StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(appUser))
                {
                    logger.LogInformation(
                        "Development bootstrap: skipped {Script}, which grants rights to a service account. "
                        + "Set {Key} to apply it. The GRANT/DENY rules it installs are therefore not in force "
                        + "locally — a developer connecting as an administrator bypasses them regardless, so "
                        + "verify them on UAT via db/Deploy-Database.ps1, not here.",
                        Path.GetFileName(path),
                        AppUserKey);
                    continue;
                }

                sql = sql.Replace(AppUserVariable, appUser, StringComparison.Ordinal);
            }

            // GO is sqlcmd's batch separator, not T-SQL — ADO.NET has to split on it itself.
            foreach (var batch in SplitBatches(sql))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = batch;
                command.CommandTimeout = 120;
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await StampSchemaVersionAsync(connection, Path.GetFileName(path), BitstreamDbContext.ExpectedSchemaVersion).ConfigureAwait(false);
            logger.LogInformation("Development bootstrap: applied {Script}.", Path.GetFileName(path));
        }

        logger.LogInformation("Development bootstrap: schema at version {Version}.", BitstreamDbContext.ExpectedSchemaVersion);
    }

    /// <summary>Walks up from the content root looking for db/mssql, so it resolves from bin/ or the project directory alike.</summary>
    private static string? FindScriptDirectory(string contentRootPath)
    {
        var directory = new DirectoryInfo(contentRootPath);

        for (var depth = 0; directory is not null && depth < 6; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "db", "mssql");

            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> SplitBatches(string sql) =>
        Regex.Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5))
            .Select(batch => batch.Trim())
            .Where(batch => batch.Length > 0);

    private static async Task StampSchemaVersionAsync(DbConnection connection, string scriptName, int schemaVersion)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            MERGE ops.SchemaVersion AS target
            USING (VALUES (@ScriptName, @SchemaVersion)) AS source (ScriptName, SchemaVersion)
                ON target.ScriptName = source.ScriptName
            WHEN MATCHED THEN UPDATE SET SchemaVersion = source.SchemaVersion, AppliedAt = SYSDATETIMEOFFSET(), AppliedBy = SUSER_SNAME()
            WHEN NOT MATCHED BY TARGET THEN INSERT (ScriptName, SchemaVersion) VALUES (source.ScriptName, source.SchemaVersion);
            """;

        AddParameter(command, "@ScriptName", scriptName);
        AddParameter(command, "@SchemaVersion", schemaVersion);

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static async Task SeedAdministratorAsync(
        BitstreamDbContext dbContext,
        IServiceProvider services,
        IConfiguration configuration,
        ILogger logger)
    {
        var email = configuration["Development:AdminEmail"] ?? "admin@bitstream.local";
        var password = configuration["Development:AdminPassword"];

        if (string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning("Development bootstrap: Development:AdminPassword is not set; no administrator seeded.");
            return;
        }

        if (await dbContext.Users.AnyAsync(user => user.Email == email).ConfigureAwait(false))
        {
            logger.LogInformation("Development bootstrap: administrator {Email} already exists.", email);
            return;
        }

        // The Administrator role and its permission grants come from
        // 0007_seed_roles_permissions.sql, which the pass above has just applied.
        var role = await dbContext.Roles.FirstOrDefaultAsync(r => r.Name == "Administrator").ConfigureAwait(false);

        if (role is null)
        {
            logger.LogWarning("Development bootstrap: the Administrator role is missing; no administrator seeded.");
            return;
        }

        var passwordHasher = services.GetRequiredService<IPasswordHasher>();

        // UserName/NormalizedUserName/NormalizedEmail are ordinarily set by UserManager.CreateAsync
        // (via the registered ILookupNormalizer); this insert bypasses UserManager entirely, so
        // they are set by hand here — otherwise UserManager.FindByEmailAsync (which queries by
        // NormalizedEmail) would never find this seeded account, and login would fail.
        var normalizedEmail = email.ToUpperInvariant();

        dbContext.Users.Add(new User
        {
            IspId = null,
            FullName = "Development Administrator",
            Email = email,
            UserName = email,
            NormalizedEmail = normalizedEmail,
            NormalizedUserName = normalizedEmail,
            Mobile = "+355690000000",
            RoleId = role.Id,
            Status = UserStatus.Active,
            PasswordHash = passwordHasher.Hash(password),
            PasswordHashAlgorithm = "argon2id",
            PasswordUpdatedAt = DateTimeOffset.UtcNow,
            TwoFactorEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow
        });

        await dbContext.SaveChangesAsync().ConfigureAwait(false);

        // No TOTP key seeded: exactly like every other user, the authenticator key is generated
        // lazily on this account's first login (AuthController.Login sees
        // GetAuthenticatorKeyAsync return null and shows the QR code then) — no special-cased dev
        // secret to keep working across re-seeds any more, since a real login is just as fast.
        logger.LogInformation(
            "Development bootstrap: seeded administrator {Email}. The Login page will show a QR " +
            "code for the second factor on first sign-in.",
            email);
    }
}
