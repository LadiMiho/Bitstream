using Bitstream.Application.Services.Dashboard;
using Bitstream.Web.Security;
using Microsoft.AspNetCore.Mvc;

namespace Bitstream.Web.Controllers;

/// <summary>
/// The landing page after sign-in: key figures and charts for the activation flow. Scoping is
/// decided by <see cref="IDashboardService"/>: an ISP user sees only their own ISP's figures.
/// </summary>
[Route("Dashboard")]
public sealed class DashboardController : Controller
{
    private readonly IDashboardService _dashboardService;

    public DashboardController(IDashboardService dashboardService) => _dashboardService = dashboardService;

    [HttpGet("")]
    [RequireSession]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        ViewData["Title"] = "Dashboard";

        return View(await _dashboardService.GetAsync(cancellationToken).ConfigureAwait(false));
    }
}
