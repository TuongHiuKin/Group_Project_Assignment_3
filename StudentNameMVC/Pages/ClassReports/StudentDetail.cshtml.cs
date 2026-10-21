using System.Security.Claims;
using AIVES.BLL.Interfaces.ExamReporting;
using AIVES.BLL.Models.ExamReporting;
using AIVES.BLL.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace StudentNameMVC.Pages.ClassReports;

[Authorize(Roles = $"{ApplicationRoles.Admin},{ApplicationRoles.Lecturer}")]
public class StudentDetailModel : PageModel
{
    private readonly IClassReportService _classReportService;

    public StudentDetailModel(IClassReportService classReportService)
    {
        _classReportService = classReportService ?? throw new ArgumentNullException(nameof(classReportService));
    }

    public StudentReportDto Report { get; set; } = new();
    public string SelectedTeacherId { get; set; } = "GV01";
    public string SelectedClassId { get; set; } = "EXAM01";
    public string SelectedExamId { get; set; } = "EXAM01";
    public int? SelectedAttemptOrdinal { get; set; }

    public async Task<IActionResult> OnGetAsync(
        string studentId,
        string classId,
        string examId,
        int? attemptOrdinal = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(studentId) || string.IsNullOrWhiteSpace(classId) || string.IsNullOrWhiteSpace(examId))
        {
            TempData["ErrorMessage"] = "Thông tin truy vấn không hợp lệ.";
            return RedirectToPage("/ClassReports/Index");
        }

        SelectedTeacherId = ResolveTeacherId();
        SelectedClassId = classId;
        SelectedExamId = examId;
        SelectedAttemptOrdinal = attemptOrdinal;

        try
        {
            var report = await _classReportService.GetStudentExamDetailForTeacherAsync(
                SelectedTeacherId,
                classId,
                examId,
                studentId,
                ct);

            if (report == null)
            {
                TempData["ErrorMessage"] = "Không tìm thấy dữ liệu bài thi của sinh viên.";
                return RedirectToPage("/ClassReports/Index", new { classId, examId, attemptOrdinal });
            }

            Report = report;
            return Page();
        }
        catch (UnauthorizedAccessException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
            return RedirectToPage("/ClassReports/Index", new { classId, examId, attemptOrdinal });
        }
        catch (InvalidOperationException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
            return RedirectToPage("/ClassReports/Index", new { classId, examId, attemptOrdinal });
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = $"Lỗi khi tải chi tiết bài thi: {ex.Message}";
            return RedirectToPage("/ClassReports/Index", new { classId, examId, attemptOrdinal });
        }
    }

    private string ResolveTeacherId(string? requestedTeacherId = null)
    {
        if (User?.IsInRole(ApplicationRoles.Admin) == true && !string.IsNullOrWhiteSpace(requestedTeacherId))
        {
            return requestedTeacherId;
        }

        var email = User?.FindFirst(ClaimTypes.Email)?.Value;
        if (!string.IsNullOrWhiteSpace(email))
        {
            if (email.Contains("lecturer2", StringComparison.OrdinalIgnoreCase) || email.Contains("gv02", StringComparison.OrdinalIgnoreCase))
            {
                return "GV02";
            }
            if (email.Contains("lecturer", StringComparison.OrdinalIgnoreCase) || email.Contains("gv01", StringComparison.OrdinalIgnoreCase))
            {
                return "GV01";
            }
        }

        var subId = User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrWhiteSpace(subId))
        {
            if (subId.StartsWith("GV", StringComparison.OrdinalIgnoreCase))
            {
                return subId.ToUpperInvariant();
            }
            return subId;
        }

        return "GV01";
    }
}
