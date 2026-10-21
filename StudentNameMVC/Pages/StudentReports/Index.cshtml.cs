using System.Security.Claims;
using AIVES.BLL.Interfaces.ExamReporting;
using AIVES.BLL.Models.ExamReporting;
using AIVES.BLL.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace StudentNameMVC.Pages.StudentReports;

[Authorize(Roles = $"{ApplicationRoles.Admin},{ApplicationRoles.Staff}")]
public class IndexModel : PageModel
{
    private readonly IStudentReportService _studentReportService;

    public IndexModel(IStudentReportService studentReportService)
    {
        _studentReportService = studentReportService ?? throw new ArgumentNullException(nameof(studentReportService));
    }

    public StudentReportDto? Report { get; set; }
    public List<ExamSettingsSnapshot> AvailableExams { get; set; } = new();
    public string SelectedExamId { get; set; } = "EXAM01";
    public string SelectedStudentId { get; set; } = "SV01";

    public async Task<IActionResult> OnGetAsync(
        string? studentId = null,
        string? examId = "EXAM01",
        CancellationToken ct = default)
    {
        AvailableExams = await _studentReportService.GetAvailableExamsAsync(ct);
        SelectedExamId = !string.IsNullOrWhiteSpace(examId)
            ? examId
            : (AvailableExams.FirstOrDefault()?.ExamId ?? "EXAM01");

        var resolvedStudentId = ResolveStudentId(studentId);
        SelectedStudentId = resolvedStudentId;

        // DEC-11 & Security: Chặn sinh viên (Staff) tự ý đổi studentId trên URL để xem bài của người khác (chống IDOR)
        if (User?.IsInRole(ApplicationRoles.Admin) != true && !string.IsNullOrWhiteSpace(studentId) &&
            !string.Equals(studentId, resolvedStudentId, StringComparison.OrdinalIgnoreCase))
        {
            return Forbid();
        }

        Report = await _studentReportService.GetStudentReportAsync(resolvedStudentId, SelectedExamId, ct);
        if (Report == null)
        {
            TempData["ErrorMessage"] = $"Không tìm thấy thông tin bài thi {SelectedExamId}.";
        }

        return Page();
    }

    internal string ResolveStudentId(string? requestedStudentId = null)
    {
        if (User?.IsInRole(ApplicationRoles.Admin) == true && !string.IsNullOrWhiteSpace(requestedStudentId))
        {
            return requestedStudentId;
        }

        var subId = User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrWhiteSpace(subId))
        {
            if (subId.StartsWith("SV", StringComparison.OrdinalIgnoreCase))
            {
                return subId.ToUpperInvariant();
            }
            if (int.TryParse(subId, out _))
            {
                return subId;
            }
        }

        var email = User?.FindFirst(ClaimTypes.Email)?.Value;
        if (!string.IsNullOrWhiteSpace(email))
        {
            if (email.Contains("sv02", StringComparison.OrdinalIgnoreCase) || email.Contains("student2", StringComparison.OrdinalIgnoreCase))
                return "SV02";
            if (email.Contains("sv03", StringComparison.OrdinalIgnoreCase) || email.Contains("student3", StringComparison.OrdinalIgnoreCase))
                return "SV03";
            if (email.Contains("sv01", StringComparison.OrdinalIgnoreCase) || email.Contains("student", StringComparison.OrdinalIgnoreCase))
                return "SV01";
        }

        if (User?.IsInRole(ApplicationRoles.Admin) == true)
        {
            return !string.IsNullOrWhiteSpace(requestedStudentId) ? requestedStudentId : "SV01";
        }

        return !string.IsNullOrWhiteSpace(subId) ? subId : "SV01";
    }
}
