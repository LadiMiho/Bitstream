using System.Data;
using System.Text.RegularExpressions;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
    private readonly BitstreamDbContext _dbContext;
    private readonly IOptionsMonitor<IdentifierOptions> _options;

    public SqlPublicIdentifierGenerator(BitstreamDbContext dbContext, IOptionsMonitor<IdentifierOptions> options)
    {
        _dbContext = dbContext;
        _options = options;
    }

    public Task<string> NextAsync(IdentifierSeries series, CancellationToken cancellationToken = default) =>
        ExecuteAsync("ops.usp_NextPublicIdentifier", "@SeriesCode", 50, series.ToString(), cancellationToken);

    /// <summary>Calls <c>ops.usp_NextPrefixedIdentifier</c> (db/mssql/0018_isp_ticket_code.sql).</summary>
    public Task<string> NextForPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        return ExecuteAsync("ops.usp_NextPrefixedIdentifier", "@Prefix", 20, prefix, cancellationToken);
    }

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
    [GeneratedRegex("^[A-Z]+_[0-9]+$", RegexOptions.CultureInvariant)]
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
