using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Bitstream.Api.Tests.Activation;

/// <summary>
/// The tests run on EF InMemory, which ignores SQL CHECK constraints, so a constraint that
/// disagrees with the ID format would only fail on a real database. This reads the latest
/// definition of CK_ActivationRequest_PublicId from db/mssql, turns its LIKE conditions into
/// regular expressions and checks them against IDs the portal issues (and must refuse).
/// </summary>
public sealed partial class ActivationPublicIdConstraintTests
{
    [Theory]
    [InlineData("TRING001")]
    [InlineData("TRINGGPON012")]
    [InlineData("TRING1000")]
    [InlineData("TRING_001")]
    [InlineData("TRINGGPON_011")]
    public void The_constraint_accepts_IDs_with_or_without_the_underscore(string publicId) =>
        Assert.True(Satisfies(publicId), $"{publicId} should satisfy CK_ActivationRequest_PublicId.");

    [Theory]
    [InlineData("TRING")]
    [InlineData("001")]
    [InlineData("TRING_")]
    [InlineData("TRING__1")]
    [InlineData("TRING01A")]
    [InlineData("TRING0_1")]
    [InlineData("tring001")]
    [InlineData("TRING-001")]
    public void The_constraint_refuses_malformed_IDs(string publicId) =>
        Assert.False(Satisfies(publicId), $"{publicId} should not satisfy CK_ActivationRequest_PublicId.");

    private static bool Satisfies(string publicId) =>
        Conditions().All(condition =>
            condition.Negated != Like(condition.LastCharacterOnly ? publicId[^1..] : publicId, condition.Pattern));

    /// <summary>The LIKE / NOT LIKE conditions of the last script in db/mssql that defines the constraint.</summary>
    private static IReadOnlyList<(bool LastCharacterOnly, bool Negated, string Pattern)> Conditions()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Bitstream.sln")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository root not found.");
        }

        var script = Directory.GetFiles(Path.Combine(root, "db", "mssql"), "*.sql")
            .Order(StringComparer.Ordinal)
            .Last(path => File.ReadAllText(path).Contains("CONSTRAINT CK_ActivationRequest_PublicId CHECK", StringComparison.Ordinal));

        var sql = File.ReadAllText(script);
        var start = sql.IndexOf("CONSTRAINT CK_ActivationRequest_PublicId CHECK", StringComparison.Ordinal);
        // Up to the next constraint of the same table, or the end of the batch.
        var next = sql.IndexOf("CONSTRAINT", start + 1, StringComparison.Ordinal);
        var batchEnd = sql.IndexOf("\nGO", start, StringComparison.Ordinal);
        var end = new[] { next, batchEnd, sql.Length }.Where(i => i > start).Min();
        var definition = sql[start..end];

        var conditions = LikeCondition().Matches(definition)
            .Select(m => (LastCharacterOnly: m.Groups["right"].Success, Negated: m.Groups["not"].Success, Pattern: m.Groups["pattern"].Value))
            .ToList();

        // Every LIKE in the definition must be one this reader understands, or the test would pass vacuously.
        Assert.NotEmpty(conditions);
        Assert.Equal(Regex.Count(definition, @"\bLIKE\b"), conditions.Count);
        return conditions;
    }

    /// <summary>T-SQL LIKE under a binary collation: % any run, _ one character, [..] a class, [^..] its complement.</summary>
    private static bool Like(string value, string pattern)
    {
        var regex = new StringBuilder("^");

        for (var i = 0; i < pattern.Length; i++)
        {
            switch (pattern[i])
            {
                case '%':
                    regex.Append(".*");
                    break;
                case '_':
                    regex.Append('.');
                    break;
                case '[':
                    var close = pattern.IndexOf(']', i + 1);
                    regex.Append('[').Append(pattern[(i + 1)..close].Replace("\\", "\\\\", StringComparison.Ordinal)).Append(']');
                    i = close;
                    break;
                default:
                    regex.Append(Regex.Escape(pattern[i].ToString()));
                    break;
            }
        }

        return Regex.IsMatch(value, regex.Append('$').ToString(), RegexOptions.CultureInvariant);
    }

    /// <summary><c>PublicId [COLLATE x] [NOT] LIKE '...'</c>, or the same on <c>RIGHT(PublicId, 1)</c>.</summary>
    [GeneratedRegex(@"(?:(?<right>RIGHT\(PublicId,\s*1\))|PublicId)(?:\s+COLLATE\s+\w+)?\s+(?<not>NOT\s+)?LIKE\s+'(?<pattern>[^']*)'", RegexOptions.CultureInvariant)]
    private static partial Regex LikeCondition();
}
