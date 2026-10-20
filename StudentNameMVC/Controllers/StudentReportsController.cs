using System.Security.Claims;
using AIVES.BLL.Interfaces.ExamReporting;
using AIVES.BLL.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace StudentNameMVC.Controllers;

[Authorize(Roles = $"{ApplicationRoles.Admin},{ApplicationRoles.Lecturer},{ApplicationRoles.Staff}")]
public class StudentReportsController : Controller
{
    private readonly IStudentReportService _studentReportService;

    public StudentReportsController(IStudentReportService studentReportService)
    {
        _studentReportService = studentReportService ?? throw new ArgumentNullException(nameof(studentReportService));
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        string? studentId = null,
        string? examId = null,
        CancellationToken ct = default)
    {
        var resolvedStudentId = ResolveStudentId(studentId);

        var isEquivalent = (string.Equals(studentId, "1", StringComparison.OrdinalIgnoreCase) && string.Equals(resolvedStudentId, "SV01", StringComparison.OrdinalIgnoreCase)) ||
                           (string.Equals(studentId, "2", StringComparison.OrdinalIgnoreCase) && string.Equals(resolvedStudentId, "SV02", StringComparison.OrdinalIgnoreCase)) ||
                           (string.Equals(studentId, "3", StringComparison.OrdinalIgnoreCase) && string.Equals(resolvedStudentId, "SV03", StringComparison.OrdinalIgnoreCase));

        // Security: Chặn sinh viên (Staff) tự ý đổi studentId trên URL để xem bài của người khác (chống IDOR). Admin và Lecturer được phép xem.
        if (!User.IsInRole(ApplicationRoles.Admin) && !User.IsInRole(ApplicationRoles.Lecturer) && !string.IsNullOrWhiteSpace(studentId) &&
            !string.Equals(studentId, resolvedStudentId, StringComparison.OrdinalIgnoreCase) && !isEquivalent)
        {
            return Forbid();
        }

        var exams = await _studentReportService.GetAvailableExamsAsync(ct);
        if (string.IsNullOrWhiteSpace(examId) || string.Equals(examId, "EXAM01", StringComparison.OrdinalIgnoreCase) || string.Equals(examId, "EXAM02", StringComparison.OrdinalIgnoreCase))
        {
            examId = exams.FirstOrDefault()?.ExamId ?? "VIVA-2580";
        }

        ViewBag.AvailableExams = exams;
        ViewBag.SelectedExamId = examId;
        ViewBag.SelectedStudentId = resolvedStudentId;

        var report = await _studentReportService.GetStudentReportAsync(resolvedStudentId, examId, ct);
        if (report == null)
        {
            TempData["ErrorMessage"] = $"Không tìm thấy thông tin bài thi {examId}.";
            return View();
        }

        return View(report);
    }

    private string ResolveStudentId(string? requestedStudentId = null)
    {
        // Admin và Giảng viên có thể xem thay mặt sinh viên bất kỳ
        if ((User.IsInRole(ApplicationRoles.Admin) || User.IsInRole(ApplicationRoles.Lecturer)) && !string.IsNullOrWhiteSpace(requestedStudentId))
        {
            return requestedStudentId;
        }

        // Lấy định danh từ Claims của tài khoản đang đăng nhập
        var email = User.FindFirst(ClaimTypes.Email)?.Value;
        if (!string.IsNullOrWhiteSpace(email))
        {
            if (email.Contains("sv02", StringComparison.OrdinalIgnoreCase) || email.Contains("student2", StringComparison.OrdinalIgnoreCase))
                return "SV02";
            if (email.Contains("sv03", StringComparison.OrdinalIgnoreCase) || email.Contains("student3", StringComparison.OrdinalIgnoreCase))
                return "SV03";
            if (email.Contains("sv01", StringComparison.OrdinalIgnoreCase) || email.Contains("student", StringComparison.OrdinalIgnoreCase))
                return "SV01";
        }

        var subId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrWhiteSpace(subId) && subId.StartsWith("SV", StringComparison.OrdinalIgnoreCase))
        {
            return subId.ToUpperInvariant();
        }

        if (User.IsInRole(ApplicationRoles.Admin) || User.IsInRole(ApplicationRoles.Lecturer))
        {
            return !string.IsNullOrWhiteSpace(requestedStudentId) ? requestedStudentId : "SV01";
        }

        return "SV01";
    }
}
