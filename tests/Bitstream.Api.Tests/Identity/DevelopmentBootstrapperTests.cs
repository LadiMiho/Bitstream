using Bitstream.Web;
using Xunit;

namespace Bitstream.Api.Tests.Identity;

/// <summary>
/// Which db/mssql folder the Development bootstrap uses, and its refusal to replay an older copy
/// of the scripts over a newer database (an older 0017 put back packages that 0031 removed).
/// </summary>
public sealed class DevelopmentBootstrapperTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bootstrap-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void A_folder_as_new_as_the_database_is_used()
    {
        var missing = DevelopmentBootstrapper.ScriptsMissingLocally(
            ["0001_schemas_and_version.sql", "0031_remove_placeholder_packages.sql"],
            ["0001_schemas_and_version.sql", "0031_remove_placeholder_packages.sql", "0032_next.sql"]);

        Assert.Empty(missing);
    }

    [Fact]
    public void A_folder_older_than_the_database_is_reported_with_the_scripts_it_lacks()
    {
        var missing = DevelopmentBootstrapper.ScriptsMissingLocally(
            ["0001_schemas_and_version.sql", "0031_remove_placeholder_packages.sql", "0030_notification_emails.sql"],
            ["0001_schemas_and_version.sql", "0017_activation_catalogues.sql"]);

        Assert.Equal(["0030_notification_emails.sql", "0031_remove_placeholder_packages.sql"], missing);
    }

    [Fact]
    public void A_database_the_scripts_never_ran_on_accepts_any_folder()
    {
        Assert.Empty(DevelopmentBootstrapper.ScriptsMissingLocally([], ["0001_schemas_and_version.sql"]));
    }

    [Fact]
    public void The_published_sites_own_scripts_win_over_an_older_folder_further_up()
    {
        // C:\inetpub\db\mssql (an old copy) above C:\inetpub\portal\db\mssql (published with the site).
        var stray = Directory.CreateDirectory(Path.Combine(_root, "db", "mssql")).FullName;
        var site = Directory.CreateDirectory(Path.Combine(_root, "portal")).FullName;
        var own = Directory.CreateDirectory(Path.Combine(site, "db", "mssql")).FullName;

        Assert.Equal(own, DevelopmentBootstrapper.FindScriptDirectory(site));

        Directory.Delete(own, recursive: true);
        Assert.Equal(stray, DevelopmentBootstrapper.FindScriptDirectory(site));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
