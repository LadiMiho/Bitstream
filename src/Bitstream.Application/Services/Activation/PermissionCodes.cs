namespace Bitstream.Application.Services.Activation;

/// <summary>
/// Permission codes this module's endpoints check, seeded in
/// <c>db/mssql/0007_seed_roles_permissions.sql</c>. See
/// <see cref="Bitstream.Application.Services.Identity.PermissionCodes"/> for the identity
/// module's codes and the reasoning for splitting them by module.
/// </summary>
public static class ActivationPermissionCodes
{
    public const string ActivationCreate = "activation.create";

    /// <summary>Read activation requests of the caller's own ISP — not needed by an ISP user to read their own; ownership, not permission (TR-SEC-18 pattern).</summary>
    public const string ActivationReadOwn = "activation.read.own";

    /// <summary>Read activation requests of any ISP.</summary>
    public const string ActivationReadAll = "activation.read.all";

    /// <summary>Record the manual GIS verification outcome (TR-ACT-12 to TR-ACT-19).</summary>
    public const string ActivationGisRecord = "activation.gis.record";

    /// <summary>Maintain package offers — the package + contract duration codes (db/mssql/0020_catalogue_manage_permission.sql).</summary>
    public const string CatalogueManage = "catalogue.manage";

    /// <summary>Confirm whether a line CRM activated actually works — the ISP's operator, or an Administrator on their behalf (db/mssql/0021_activation_confirmation.sql).</summary>
    public const string ActivationConfirm = "activation.confirm";

    /// <summary>Record the service desk's final outcome after the operator reported the line not working (db/mssql/0021_activation_confirmation.sql).</summary>
    public const string ActivationServiceDeskDecide = "activation.servicedesk.decide";
}
