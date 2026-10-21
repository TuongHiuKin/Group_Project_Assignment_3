using AIVES.BLL.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace StudentNameMVC.Pages.Account;

[Authorize(Roles = ApplicationRoles.Lecturer)]
public class LecturerDashboardModel : PageModel
{
    public void OnGet()
    {
    }
}
