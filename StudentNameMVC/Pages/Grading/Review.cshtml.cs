using AIVES.BLL.Interfaces.ExamReporting;
using AIVES.BLL.Interfaces.Grading;
using AIVES.BLL.Models.ExamReporting;
using AIVES.BLL.Models.Grading;
using AIVES.BLL.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using GradingState = AIVES.BLL.Models.Grading.GradingState;

namespace StudentNameMVC.Pages.Grading;

[Authorize(Roles = $"{ApplicationRoles.Lecturer},{ApplicationRoles.Admin}")]
public class ReviewModel : PageModel
{
    private readonly IGradingOrchestrator _gradingOrchestrator;
    private readonly IClassReportService _classReportService;
    private readonly ILogger<ReviewModel> _logger;

    public ReviewModel(
        IGradingOrchestrator gradingOrchestrator,
        IClassReportService classReportService,
        ILogger<ReviewModel> logger)
    {
        _gradingOrchestrator = gradingOrchestrator ?? throw new ArgumentNullException(nameof(gradingOrchestrator));
        _classReportService = classReportService ?? throw new ArgumentNullException(nameof(classReportService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [BindProperty(SupportsGet = true)]
    public Guid? SessionId { get; set; }

    public SessionGradingSummaryDto? Session { get; set; }
    public string ExamTitle { get; set; } = string.Empty;
    public string StudentDisplayName { get; set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(Guid? sessionId, CancellationToken ct = default)
    {
        var targetSessionId = sessionId ?? SessionId;
        if (!targetSessionId.HasValue || targetSessionId.Value == Guid.Empty)
        {
            _logger.LogWarning("ReviewModel: Truy cập thiếu sessionId.");
            SetErrorMessage("Không tìm thấy mã phiên thi cần xem.");
            return RedirectToPage("/Grading/Index");
        }

        SessionId = targetSessionId;

        try
        {
            Session = await _gradingOrchestrator.GetSessionGradingAsync(targetSessionId.Value, ct);
            if (Session == null)
            {
                _logger.LogWarning("ReviewModel: Không tìm thấy phiên thi {SessionId}", targetSessionId.Value);
                SetErrorMessage($"Không tìm thấy dữ liệu chấm thi cho phiên {targetSessionId.Value}.");
                return RedirectToPage("/Grading/Index");
            }

            // Lấy tiêu đề đề thi
            var exams = await _classReportService.GetAvailableExamsAsync(ct);
            var matchExam = exams.FirstOrDefault(e => string.Equals(e.ExamId, Session.ExamId, StringComparison.OrdinalIgnoreCase));
            ExamTitle = matchExam != null ? $"{matchExam.ExamTitle} ({matchExam.ExamId})" : Session.ExamId;

            // Định dạng tên sinh viên hiển thị
            StudentDisplayName = !string.IsNullOrWhiteSpace(Session.StudentName) && !string.Equals(Session.StudentName, Session.StudentId, StringComparison.OrdinalIgnoreCase)
                ? $"{Session.StudentName} ({Session.StudentId})"
                : Session.StudentId;

            return Page();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi khi tải chi tiết bài thi chấm {SessionId}", targetSessionId.Value);
            SetErrorMessage("Có lỗi xảy ra khi tải dữ liệu bài thi. Vui lòng thử lại sau.");
            return RedirectToPage("/Grading/Index");
        }
    }

    public string GetSpeakingSpeedAssessment(double wpm)
    {
        if (wpm <= 0) return "Chưa xác định";
        if (wpm < 90) return "Chậm (Cần tăng tốc độ phản xạ)";
        if (wpm <= 160) return "Chuẩn (Nhịp độ tự nhiên, rõ ràng)";
        return "Nhanh (Cần kiểm soát ngắt nghỉ)";
    }

    private void SetErrorMessage(string message)
    {
        if (TempData != null)
        {
            TempData["ErrorMessage"] = message;
        }
    }
}
