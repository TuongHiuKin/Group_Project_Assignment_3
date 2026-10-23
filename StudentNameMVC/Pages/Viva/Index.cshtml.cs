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

    public void OnGet()
    {
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
