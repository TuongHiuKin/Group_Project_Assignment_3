using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AIVES.BLL.Interfaces.VivaInterview;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace StudentNameMVC.Pages.Viva;

[Authorize]
public class IndexModel : PageModel
{
    private readonly IVivaSessionOrchestrator? _orchestrator;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(ILogger<IndexModel> logger) : this(logger, null)
    {
    }

    [ActivatorUtilitiesConstructor]
    public IndexModel(ILogger<IndexModel> logger, IServiceProvider? serviceProvider)
    {
        _logger = logger;
        _orchestrator = serviceProvider?.GetService<IVivaSessionOrchestrator>();
    }

    [BindProperty]
    public string? SessionIdInput { get; set; }

    [BindProperty]
    public string ExamIdInput { get; set; } = "EXAM01";

    public string? ErrorMessage { get; set; }

    public bool IsOrchestratorAvailable => _orchestrator != null;

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
