using AIVES.BLL.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace StudentNameMVC.Pages.Account;

[Authorize]
public class DashboardModel : PageModel
{
    public IActionResult OnGet()
    {
        if (User.IsInRole(ApplicationRoles.Admin))
        {
            return RedirectToPage("/Account/AdminDashboard");
        }

        if (User.IsInRole(ApplicationRoles.Lecturer))
        {
            return RedirectToPage("/Account/LecturerDashboard");
        }

        return RedirectToPage("/Account/StudentDashboard");
    }
}
