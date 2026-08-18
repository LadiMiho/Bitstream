using Microsoft.AspNetCore.Mvc;

namespace Bitstream.Api.Pages;

/// <summary>Landing route. There is no dashboard of its own yet, so an authenticated visitor lands on the first module.</summary>
public sealed class IndexModel : SecurePageModel
{
    public IActionResult OnGet() => RedirectToPage("/AccessManagement/Index");
}
