using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace StudentNameMVC.Pages.Report;

[Authorize(Roles = "Admin")]
public class AdminReportModel : PageModel
{
    public IActionResult OnGet(DateTime? startDate, DateTime? endDate)
    {
        return RedirectToPage("/Report/Index", new { startDate, endDate });
    }
}
