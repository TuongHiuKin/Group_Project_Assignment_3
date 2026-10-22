using AIVES.BLL.Interfaces;
using AIVES.BLL.Interfaces.ExamReporting;
using AIVES.BLL.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using StudentNameMVC.ViewModels;

namespace StudentNameMVC.Pages.Account;

[Authorize(Roles = ApplicationRoles.Admin)]
public class AdminDashboardModel : PageModel
{
    private readonly ISystemAccountService _accountService;
    private readonly IStudentReportService _studentReportService;

    public AdminDashboardModel(
        ISystemAccountService accountService,
        IStudentReportService studentReportService)
    {
        _accountService = accountService ?? throw new ArgumentNullException(nameof(accountService));
        _studentReportService = studentReportService ?? throw new ArgumentNullException(nameof(studentReportService));
    }

    public int TotalAccounts { get; set; }
    public int TotalStudents { get; set; }
    public int TotalLecturers { get; set; }
    public int TotalExams { get; set; }
    public List<AccountItemViewModel> RecentAccounts { get; set; } = new();

    public async Task OnGetAsync(CancellationToken ct = default)
    {
        var accounts = await _accountService.SearchAccountsAsync(includeDeleted: false, ct: ct);
        TotalAccounts = accounts.Count;
        TotalStudents = accounts.Count(a => a.AccountRole == 1);
        TotalLecturers = accounts.Count(a => a.AccountRole == 2);

        var exams = await _studentReportService.GetAvailableExamsAsync(ct);
        TotalExams = exams.Count;

        RecentAccounts = accounts
            .OrderByDescending(a => a.AccountId)
            .Take(5)
            .Select(a => new AccountItemViewModel
            {
                AccountId = a.AccountId,
                AccountName = a.AccountName,
                AccountEmail = a.AccountEmail,
                AccountRole = a.AccountRole,
                IsDeleted = a.IsDeleted
            })
            .ToList();
    }
}
