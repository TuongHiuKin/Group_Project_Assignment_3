using System.Security.Claims;
using AIVES.BLL.Interfaces.ExamReporting;
using AIVES.BLL.Models.ExamReporting;
using AIVES.BLL.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace StudentNameMVC.Pages.Account;

[Authorize(Roles = ApplicationRoles.Staff)]
public class StudentDashboardModel : PageModel
{
    private readonly IStudentReportService _studentReportService;

    public StudentDashboardModel(IStudentReportService studentReportService)
    {
        _studentReportService = studentReportService ?? throw new ArgumentNullException(nameof(studentReportService));
    }

    public List<ExamSettingsSnapshot> AvailableExams { get; set; } = new();
    public StudentReportDto? StudentReport { get; set; }
    public string SelectedExamId { get; set; } = string.Empty;
    public string StudentId { get; set; } = string.Empty;

    public async Task OnGetAsync(string? examId = null, CancellationToken ct = default)
    {
        StudentId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.Identity?.Name
            ?? string.Empty;

        AvailableExams = await _studentReportService.GetAvailableExamsAsync(ct);

        SelectedExamId = !string.IsNullOrWhiteSpace(examId)
            ? examId
            : (AvailableExams.FirstOrDefault()?.ExamId ?? string.Empty);

        if (!string.IsNullOrWhiteSpace(StudentId) && !string.IsNullOrWhiteSpace(SelectedExamId))
        {
            StudentReport = await _studentReportService.GetStudentReportAsync(StudentId, SelectedExamId, ct);
        }
    }
}
