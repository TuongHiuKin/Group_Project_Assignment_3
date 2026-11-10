using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Interfaces.VivaInterview;
using AIVES.BLL.Options;
using AIVES.BLL.Services.Ai;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace StudentNameMVC.Pages.Viva;

[Authorize]
public class IndexModel : PageModel
{
    private readonly IVivaSessionOrchestrator? _orchestrator;
    private readonly IAiCredentialStore? _credentialStore;
    private readonly IOptions<AiServiceOptions>? _aiOptions;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        ILogger<IndexModel> logger,
        IVivaSessionOrchestrator? orchestrator = null,
        IAiCredentialStore? credentialStore = null,
        IOptions<AiServiceOptions>? aiOptions = null)
    {
        _logger = logger;
        _orchestrator = orchestrator;
        _credentialStore = credentialStore;
        _aiOptions = aiOptions;
    }

    [BindProperty]
    public string? SessionIdInput { get; set; }

    [BindProperty]
    public string ExamIdInput { get; set; } = "EXAM01";

    public string? ErrorMessage { get; set; }

    public bool IsOrchestratorAvailable => _orchestrator != null;

    /// <summary>Only students sit exams; Admin and Lecturer accounts never create viva sessions.</summary>
    public bool IsCandidate =>
        !User.IsInRole(AIVES.BLL.Security.ApplicationRoles.Admin) &&
        !User.IsInRole(AIVES.BLL.Security.ApplicationRoles.Lecturer);

    public bool IsAiConfigured
    {
        get
        {
            if (_credentialStore == null)
            {
                return true;
            }

            var config = _credentialStore.GetConfiguration();
            if (!config.UseRealAi && _aiOptions?.Value.AllowMockFallback == true)
            {
                return true;
            }

            return config.UseRealAi && config.Profiles.Any(p =>
                !string.IsNullOrWhiteSpace(p.ApiKey) || NetworkEndpointHelper.IsLocalOrLan(p.BaseUrl));
        }
    }

    /// <summary>Số lượt đã dùng / tối đa của sinh viên hiện tại cho đề mặc định.</summary>
    public int AttemptsUsed { get; private set; }

    /// <summary>Tổng số lượt được phép làm cho đề mặc định.</summary>
    public int AttemptsMax { get; private set; } = AIVES.BLL.Services.VivaInterview.VivaSessionOrchestrator.MaxAttemptsPerExam;

    /// <summary>Sinh viên đã dùng hết lượt: ẩn nút bắt đầu thay vì để bấm rồi báo lỗi.</summary>
    public bool HasAttemptsLeft { get; private set; } = true;

    public async Task OnGetAsync(CancellationToken ct = default)
    {
        if (_orchestrator == null || !IsCandidate)
        {
            return;
        }

        var studentId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(studentId))
        {
            return;
        }

        try
        {
            var allowance = await _orchestrator.GetAttemptAllowanceAsync(ExamIdInput, studentId, ct);
            AttemptsUsed = allowance.Used;
            AttemptsMax = allowance.Max;
            HasAttemptsLeft = allowance.CanStart;
        }
        catch (Exception ex)
        {
            // Không chặn oan sinh viên chỉ vì bước đếm lượt lỗi; server vẫn có chốt chặn ở OnPostCreateAsync.
            _logger.LogWarning(ex, "Could not load attempt allowance for student {StudentId}", studentId);
        }
    }

    public IActionResult OnPost()
    {
        if (string.IsNullOrWhiteSpace(SessionIdInput))
        {
            ErrorMessage = "Vui lòng nhập mã phiên thi (Session ID).";
            return Page();
        }

        if (!Guid.TryParse(SessionIdInput.Trim(), out var sessionGuid) || sessionGuid == Guid.Empty)
        {
            ErrorMessage = "Mã phiên thi không hợp lệ (phải là định dạng GUID hợp lệ).";
            return Page();
        }

        return RedirectToPage("/Viva/Exam", new { sessionId = sessionGuid });
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken ct = default)
    {
        if (_orchestrator == null)
        {
            ErrorMessage = "Chức năng thi vấn đáp chưa được bật.";
            return Page();
        }

        if (!IsAiConfigured)
        {
            ErrorMessage = "Chưa cấu hình AI, vui lòng nhờ Admin vào Cấu hình AI.";
            return Page();
        }

        if (User.IsInRole(AIVES.BLL.Security.ApplicationRoles.Lecturer))
        {
            ErrorMessage = "Tài khoản Giảng viên không tham gia làm bài thi với tư cách thí sinh. Vui lòng đăng nhập với tài khoản Sinh viên để thi vấn đáp, hoặc truy cập Dashboard Giảng viên / Thống kê Lớp học để đánh giá kết quả.";
            return Page();
        }

        if (User.IsInRole(AIVES.BLL.Security.ApplicationRoles.Admin))
        {
            ErrorMessage = "Tài khoản Quản trị viên không tham gia thi vấn đáp. Vui lòng đăng nhập với tài khoản Sinh viên để thi.";
            return Page();
        }

        var studentId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(studentId))
        {
            return Challenge();
        }

        if (string.IsNullOrWhiteSpace(ExamIdInput))
        {
            ErrorMessage = "Vui lòng nhập mã đề thi (Exam ID).";
            return Page();
        }

        // Reject an unknown exam before creating anything: a typo would otherwise persist a
        // phantom session that can never start (no questions) yet still shows in the grading queue.
        if (!await _orchestrator.ExamExistsAsync(ExamIdInput.Trim(), ct))
        {
            ErrorMessage = $"Mã đề thi '{ExamIdInput.Trim()}' không tồn tại hoặc chưa có câu hỏi. Vui lòng kiểm tra lại mã đề.";
            return Page();
        }

        // Chốt chặn số lượt thi. Trước đây không có: sinh viên bấm nút là tạo được lượt mới vô hạn,
        // dù trang báo cáo vẫn ghi giới hạn 3 lượt. Đã gặp thực tế một tài khoản có 18 lượt.
        var allowance = await _orchestrator.GetAttemptAllowanceAsync(ExamIdInput.Trim(), studentId, ct);
        if (!allowance.CanStart)
        {
            ErrorMessage = $"Bạn đã dùng hết {allowance.Max} lượt thi cho đề '{ExamIdInput.Trim()}'. " +
                           "Vui lòng liên hệ giảng viên nếu cần làm lại.";
            _logger.LogWarning(
                "Student {StudentId} blocked from creating attempt {Used}/{Max} for exam {ExamId}",
                studentId, allowance.Used, allowance.Max, ExamIdInput);
            return Page();
        }

        try
        {
            var result = await _orchestrator.InitializeSessionAsync(ExamIdInput.Trim(), studentId, ct);
            return RedirectToPage("/Viva/Exam", new { sessionId = result.SessionId });
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid argument when initializing viva session for exam {ExamId}", ExamIdInput);
            ErrorMessage = ex.Message;
            return Page();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error initializing viva session for exam {ExamId}", ExamIdInput);
            ErrorMessage = "Đã xảy ra lỗi khi tạo phiên thi vấn đáp. Vui lòng thử lại sau.";
            return Page();
        }
    }
}
