using System.Text.RegularExpressions;
using Xunit;

namespace Bitstream.Api.Tests.Activation;

/// <summary>
/// The deployment re-runs every db/mssql script on each publish, so a script that seeds packages
/// puts them back every time. Packages are maintained per environment on Packages &amp; durations;
/// no script may insert them.
/// </summary>
public sealed partial class CatalogueSeedScriptTests
{
    [Fact]
    public void No_database_script_seeds_packages()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Bitstream.sln")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository root not found.");
        }

        var seeding = Directory.GetFiles(Path.Combine(root, "db", "mssql"), "*.sql")
            .Where(path => PackageInsert().IsMatch(File.ReadAllText(path)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(seeding);
    }

    /// <summary>MERGE into or INSERT into portal.Package itself (not portal.PackageOffer).</summary>
    [GeneratedRegex(@"\b(MERGE|INSERT)\s+(INTO\s+)?portal\.Package\b(?!Offer)", RegexOptions.IgnoreCase)]
    private static partial Regex PackageInsert();
}
