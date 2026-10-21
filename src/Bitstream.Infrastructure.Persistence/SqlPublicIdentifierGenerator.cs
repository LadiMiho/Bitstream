using System.Data;
using System.Text.RegularExpressions;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bitstream.Infrastructure.Persistence;

/// <summary>
/// Implements <see cref="IPublicIdentifierGenerator"/> by calling
/// <c>ops.usp_NextPublicIdentifier</c> (db/mssql/0005_identifier_series.sql).
/// <para>
/// TR-DAT-01/02b/03: the procedure allocates by taking an exclusive row lock inside the calling
/// transaction, which is what makes the series gap-free and collision-free — so this method must
/// run on the same connection and, when the caller has opened one, the same
/// <see cref="IUnitOfWork.BeginTransactionAsync"/> transaction as the business write. It is
/// invoked through raw ADO.NET rather than <c>FromSql</c> because EF Core has no first-class way
/// to read a stored procedure's output parameter.
/// </para>
/// </summary>
public sealed partial class SqlPublicIdentifierGenerator : IPublicIdentifierGenerator
{
    // Warn about an out-of-date procedure once per process, not on every request.
    private static int _staleProcedureReported;

    private readonly BitstreamDbContext _dbContext;
    private readonly IOptionsMonitor<IdentifierOptions> _options;
    private readonly ILogger<SqlPublicIdentifierGenerator> _logger;

    public SqlPublicIdentifierGenerator(
        BitstreamDbContext dbContext, IOptionsMonitor<IdentifierOptions> options, ILogger<SqlPublicIdentifierGenerator> logger)
    {
        _dbContext = dbContext;
        _options = options;
        _logger = logger;
    }

    public Task<string> NextAsync(IdentifierSeries series, CancellationToken cancellationToken = default) =>
        ExecuteAsync("ops.usp_NextPublicIdentifier", "@SeriesCode", 50, series.ToString(), cancellationToken);

    /// <summary>Calls <c>ops.usp_NextPrefixedIdentifier</c> (db/mssql/0018_isp_ticket_code.sql).</summary>
    /// <remarks>
    /// The result is always ticket code + number (TRING001): if the database still has the 0018
    /// procedure, which adds an underscore, it is removed here and a warning says to run 0025.
    /// </remarks>
    public async Task<string> NextForPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var issued = await ExecuteAsync("ops.usp_NextPrefixedIdentifier", "@Prefix", 20, prefix, cancellationToken).ConfigureAwait(false);
        var identifier = PrefixedIdentifier.WithoutSeparator(prefix, issued);

        if (!string.Equals(identifier, issued, StringComparison.Ordinal) && Interlocked.Exchange(ref _staleProcedureReported, 1) == 0)
        {
            LogStaleProcedure(_logger, issued, identifier);
        }

        return identifier;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "ops.usp_NextPrefixedIdentifier returned {Issued}, with an underscore: the database is missing db/mssql/0025. " +
                  "The identifier was issued as {Identifier}; run 0025 to update the procedure.")]
    private static partial void LogStaleProcedure(ILogger logger, string issued, string identifier);

    private async Task<string> ExecuteAsync(
        string procedure, string inputName, int inputSize, string inputValue, CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = procedure;
        command.CommandType = CommandType.StoredProcedure;

        // TR-ACT-06 / TR-ARC-03: enlist in the caller's transaction, when one is open, so the
        // allocation commits or rolls back with the business write rather than on its own.
        if (_dbContext.Database.CurrentTransaction is { } transaction)
        {
            command.Transaction = transaction.GetDbTransaction();
        }

        var inputParameter = command.CreateParameter();
        inputParameter.ParameterName = inputName;
        inputParameter.DbType = DbType.String;
        inputParameter.Size = inputSize;
        inputParameter.Value = inputValue;
        command.Parameters.Add(inputParameter);

        var identifierParameter = command.CreateParameter();
        identifierParameter.ParameterName = "@Identifier";
        identifierParameter.DbType = DbType.String;
        identifierParameter.Size = 32;
        identifierParameter.Direction = ParameterDirection.Output;
        command.Parameters.Add(identifierParameter);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return (string)identifierParameter.Value!;
    }

    // TR-DAT-02d, mirroring IdentifierOptionsValidator's check of the same configured pattern.
    [GeneratedRegex("^[A-Z]+_?[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex DefaultPattern();

    public bool IsValid(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        var pattern = _options.CurrentValue.Pattern;

        return string.IsNullOrEmpty(pattern)
            ? DefaultPattern().IsMatch(identifier)
            : Regex.IsMatch(identifier, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }
}
