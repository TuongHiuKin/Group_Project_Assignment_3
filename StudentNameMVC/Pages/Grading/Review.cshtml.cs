using System.Security.Claims;
using AIVES.BLL.Interfaces.ExamReporting;
using AIVES.BLL.Interfaces.Grading;
using AIVES.BLL.Models.ExamReporting;
using AIVES.BLL.Models.Grading;
using AIVES.BLL.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using GradingState = AIVES.BLL.Models.Grading.GradingState;

namespace StudentNameMVC.Pages.Grading;

[Authorize(Roles = $"{ApplicationRoles.Lecturer},{ApplicationRoles.Admin}")]
[IgnoreAntiforgeryToken(Order = 1001)]
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

    public async Task<IActionResult> OnPostAcceptAiScoreAsync(
        string questionId,
        decimal aiScore,
        string? expectedRowVersion,
        CancellationToken ct = default)
    {
        if (!SessionId.HasValue || SessionId.Value == Guid.Empty)
        {
            SetErrorMessage("Mã phiên thi không hợp lệ.");
            return RedirectToPage("/Grading/Index");
        }

        if (string.IsNullOrWhiteSpace(questionId))
        {
            SetErrorMessage("Mã câu hỏi không hợp lệ.");
            return RedirectToPage(new { sessionId = SessionId });
        }

        try
        {
            var teacherId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.Identity?.Name ?? "Teacher";
            var request = new FinalizeScoreRequestDto
            {
                SessionId = SessionId.Value,
                QuestionId = questionId,
                FinalScore = aiScore,
                TeacherNotes = "Xác nhận theo điểm đề xuất của AI",
                ChangeReason = "Xác nhận điểm AI",
                ExpectedRowVersion = expectedRowVersion
            };

            await _gradingOrchestrator.FinalizeScoreAsync(request, teacherId, ct);
            SetSuccessMessage($"Đã xác nhận điểm AI ({aiScore:0.##}) cho câu hỏi {questionId} thành công.");
        }
        catch (DbUpdateConcurrencyException)
        {
            SetErrorMessage("Xung đột dữ liệu: Điểm câu hỏi này đã được cập nhật bởi giảng viên khác. Vui lòng kiểm tra lại trước khi lưu.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi khi xác nhận điểm AI cho phiên {SessionId}, câu {QuestionId}", SessionId, questionId);
            SetErrorMessage($"Không thể xác nhận điểm AI: {ex.Message}");
        }

        return RedirectToPage(new { sessionId = SessionId });
    }

    public async Task<IActionResult> OnPostOverrideScoreAsync(
        string questionId,
        decimal finalScore,
        string? teacherNotes,
        string? changeReason,
        string? expectedRowVersion,
        bool isAlreadyFinalized,
        decimal maxPoints = 10m,
        CancellationToken ct = default)
    {
        if (!SessionId.HasValue || SessionId.Value == Guid.Empty)
        {
            SetErrorMessage("Mã phiên thi không hợp lệ.");
            return RedirectToPage("/Grading/Index");
        }

        if (string.IsNullOrWhiteSpace(questionId))
        {
            SetErrorMessage("Mã câu hỏi không hợp lệ.");
            return RedirectToPage(new { sessionId = SessionId });
        }

        // Validate điểm số
        if (finalScore < 0 || finalScore > maxPoints)
        {
            SetErrorMessage($"Điểm giảng viên nhập ({finalScore:0.##}) không hợp lệ. Điểm phải nằm trong khoảng từ 0 đến {maxPoints:0.##}.");
            return RedirectToPage(new { sessionId = SessionId });
        }

        // Validate lý do sửa đổi khi đã chốt điểm
        if (isAlreadyFinalized && string.IsNullOrWhiteSpace(changeReason))
        {
            SetErrorMessage("Bắt buộc phải nhập lý do điều chỉnh khi thay đổi điểm đã được chốt trước đó.");
            return RedirectToPage(new { sessionId = SessionId });
        }

        try
        {
            var teacherId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.Identity?.Name ?? "Teacher";
            var request = new FinalizeScoreRequestDto
            {
                SessionId = SessionId.Value,
                QuestionId = questionId,
                FinalScore = finalScore,
                TeacherNotes = teacherNotes,
                ChangeReason = changeReason,
                ExpectedRowVersion = expectedRowVersion
            };

            await _gradingOrchestrator.FinalizeScoreAsync(request, teacherId, ct);
            SetSuccessMessage($"Đã chốt điểm giảng viên ({finalScore:0.##}/{maxPoints:0.##}) cho câu hỏi {questionId} thành công.");
        }
        catch (DbUpdateConcurrencyException)
        {
            SetErrorMessage("Xung đột dữ liệu: Điểm câu hỏi này đã được cập nhật bởi giảng viên khác. Vui lòng kiểm tra lại trước khi lưu.");
        }
        catch (ArgumentException argEx)
        {
            SetErrorMessage(argEx.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi khi ghi đè điểm GV cho phiên {SessionId}, câu {QuestionId}", SessionId, questionId);
            SetErrorMessage($"Không thể lưu điểm giảng viên: {ex.Message}");
        }

        return RedirectToPage(new { sessionId = SessionId });
    }

    public async Task<IActionResult> OnPostRegradeQuestionAsync(
        string questionId,
        CancellationToken ct = default)
    {
        if (!SessionId.HasValue || SessionId.Value == Guid.Empty)
        {
            SetErrorMessage("Mã phiên thi không hợp lệ.");
            return RedirectToPage("/Grading/Index");
        }

        if (string.IsNullOrWhiteSpace(questionId))
        {
            SetErrorMessage("Mã câu hỏi không hợp lệ.");
            return RedirectToPage(new { sessionId = SessionId });
        }

        try
        {
            // Kiểm tra xem câu hỏi đã bị chốt điểm hay chưa
            var currentSession = await _gradingOrchestrator.GetSessionGradingAsync(SessionId.Value, ct);
            var question = currentSession?.QuestionResults.FirstOrDefault(q => string.Equals(q.QuestionId, questionId, StringComparison.OrdinalIgnoreCase));
            if (question != null && question.IsFinalized)
            {
                SetErrorMessage($"Câu hỏi '{question.QuestionTitle}' đã chốt điểm chính thức nên không thể chấm lại bằng AI. Vui lòng mở khóa hoặc sử dụng chức năng 'Sửa điểm chính thức'.");
                return RedirectToPage(new { sessionId = SessionId });
            }

            var teacherId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.Identity?.Name ?? "Teacher";
            var result = await _gradingOrchestrator.RegradeQuestionAsync(SessionId.Value, questionId, teacherId, ct);

            SetSuccessMessage($"Đã hoàn tất chấm lại bằng AI cho câu hỏi {questionId}. Điểm AI đề xuất mới: {result.AiScore:0.##}/{result.MaxPoints:0.##}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi khi chấm lại bằng AI cho phiên {SessionId}, câu {QuestionId}", SessionId, questionId);
            SetErrorMessage($"Không thể chấm lại bằng AI: {ex.Message}");
        }

        return RedirectToPage(new { sessionId = SessionId });
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

    private void SetSuccessMessage(string message)
    {
        if (TempData != null)
        {
            TempData["SuccessMessage"] = message;
        }
    }
}
