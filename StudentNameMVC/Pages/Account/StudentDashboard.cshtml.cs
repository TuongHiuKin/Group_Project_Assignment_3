using AIVES.BLL.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace StudentNameMVC.Pages.Account;

[Authorize(Roles = ApplicationRoles.Staff)]
public class StudentDashboardModel : PageModel
{
    public void OnGet()
    {
    }
}
