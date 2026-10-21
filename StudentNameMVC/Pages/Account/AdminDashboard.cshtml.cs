using AIVES.BLL.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace StudentNameMVC.Pages.Account;

[Authorize(Roles = ApplicationRoles.Admin)]
public class AdminDashboardModel : PageModel
{
    public void OnGet()
    {
    }
}
