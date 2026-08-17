using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace Bitstream.Infrastructure.Persistence;

/// <summary>
/// Reads the deployed schema version from <c>ops.SchemaVersion</c>.
/// <para>
/// ADR-0002 keeps the schema in numbered T-SQL scripts rather than in EF migrations, which
/// buys reviewable DDL at the cost of the EF model and the database being able to drift.
/// This is the mechanism that stops the drift being silent: the deployed version is compared
/// with <see cref="BitstreamDbContext.ExpectedSchemaVersion"/> at start-up (fail fast) and on
/// every readiness probe (visible to monitoring).
/// </para>
/// </summary>
public static class SchemaVersion
{
    /// <summary>Query used to read the applied version. Kept here so both callers use the same one.</summary>
    public const string Query = "SELECT MAX(SchemaVersion) FROM ops.SchemaVersion;";

    /// <summary>
    /// Returns the highest applied schema version, or null when the table is empty or absent.
    /// </summary>
    /// <param name="dbContext">Context to read through.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<int?> GetDeployedSchemaVersionAsync(
        this BitstreamDbContext dbContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        var connection = dbContext.Database.GetDbConnection();
        var openedHere = false;

        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            openedHere = true;
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = Query;
            command.CommandType = CommandType.Text;

            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            return result is null or DBNull
                ? null
                : Convert.ToInt32(result, CultureInfo.InvariantCulture);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }
}
