using System.Security.Claims;
using AIVES.BLL.Interfaces.ExamReporting;
using AIVES.BLL.Models.ExamReporting;
using AIVES.BLL.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace StudentNameMVC.Pages.ClassReports;

[Authorize(Roles = $"{ApplicationRoles.Admin},{ApplicationRoles.Lecturer}")]
public class IndexModel : PageModel
{
    private readonly IClassReportService _classReportService;

    public IndexModel(IClassReportService classReportService)
    {
        _classReportService = classReportService ?? throw new ArgumentNullException(nameof(classReportService));
    }

    public ClassStatisticsDto? Statistics { get; set; }
    public List<ExamSettingsSnapshot> AvailableExams { get; set; } = new();
    public List<string> AvailableClasses { get; set; } = new();
    public string SelectedTeacherId { get; set; } = "GV01";
    public string SelectedClassId { get; set; } = "EXAM01";
    public string SelectedExamId { get; set; } = "EXAM01";
    public int? SelectedAttemptOrdinal { get; set; } = 1;

    public async Task<IActionResult> OnGetAsync(
        string? teacherId = null,
        string? classId = null,
        string? examId = null,
        int? attemptOrdinal = 1,
        CancellationToken ct = default)
    {
        SelectedTeacherId = ResolveTeacherId(teacherId);
        AvailableExams = await _classReportService.GetAvailableExamsAsync(ct);
        AvailableClasses = await _classReportService.GetAvailableClassesForTeacherAsync(SelectedTeacherId, ct);

        SelectedExamId = !string.IsNullOrWhiteSpace(examId)
            ? examId
            : (AvailableExams.FirstOrDefault()?.ExamId ?? "EXAM01");
        SelectedClassId = !string.IsNullOrWhiteSpace(classId)
            ? classId
            : (AvailableClasses.FirstOrDefault() ?? SelectedExamId);
        SelectedAttemptOrdinal = attemptOrdinal;

        try
        {
            Statistics = await _classReportService.GetClassStatisticsAsync(
                SelectedTeacherId,
                SelectedClassId,
                SelectedExamId,
                SelectedAttemptOrdinal,
                ct);
        }
        catch (UnauthorizedAccessException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
            Statistics = null;
        }

        return Page();
    }

    public async Task<IActionResult> OnGetExportGradeSheetAsync(
        string? teacherId = null,
        string? classId = null,
        string? examId = null,
        CancellationToken ct = default)
    {
        var resolvedTeacherId = ResolveTeacherId(teacherId);
        var exams = await _classReportService.GetAvailableExamsAsync(ct);
        var classes = await _classReportService.GetAvailableClassesForTeacherAsync(resolvedTeacherId, ct);

        var resolvedExamId = !string.IsNullOrWhiteSpace(examId)
            ? examId
            : (exams.FirstOrDefault()?.ExamId ?? "EXAM01");
        var resolvedClassId = !string.IsNullOrWhiteSpace(classId)
            ? classId
            : (classes.FirstOrDefault() ?? resolvedExamId);

        try
        {
            var fileBytes = await _classReportService.ExportClassGradeSheetCsvAsync(resolvedTeacherId, resolvedClassId, resolvedExamId, ct);
            var fileName = $"BangDiem_Lop_{resolvedClassId}_{resolvedExamId}.csv";
            return File(fileBytes, "text/csv; charset=utf-8", fileName);
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = $"Lỗi khi xuất bảng điểm: {ex.Message}";
            return RedirectToPage("/ClassReports/Index", new { classId = resolvedClassId, examId = resolvedExamId });
        }
    }

    internal string ResolveTeacherId(string? requestedTeacherId = null)
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
