namespace Bitstream.Infrastructure.Persistence;

/// <summary>
/// The activation request ID format: the ISP's ticket code directly followed by the number,
/// e.g. TRING001 (db/mssql/0025). Applied to what <c>ops.usp_NextPrefixedIdentifier</c> returns,
/// so a database still on the 0018 version of the procedure (TRING_001) cannot change the format.
/// </summary>
public static class PrefixedIdentifier
{
    /// <summary><c>TRING_011</c> → <c>TRING011</c>; an identifier already without the separator is returned as is.</summary>
    public static string WithoutSeparator(string prefix, string identifier)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(identifier);

        var withSeparator = prefix + "_";

        return identifier.StartsWith(withSeparator, StringComparison.Ordinal)
            ? prefix + identifier[withSeparator.Length..]
            : identifier;
    }
}
