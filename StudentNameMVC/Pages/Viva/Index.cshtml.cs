using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AIVES.BLL.Interfaces.VivaInterview;
using AIVES.BLL.Models.VivaInterview;
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
    private readonly IVivaExamRoomService? _examRoomService;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(ILogger<IndexModel> logger) : this(logger, null)
    {
    }

    [ActivatorUtilitiesConstructor]
    public IndexModel(ILogger<IndexModel> logger, IServiceProvider? serviceProvider)
    {
        _logger = logger;
        _orchestrator = serviceProvider?.GetService<IVivaSessionOrchestrator>();
        _examRoomService = serviceProvider?.GetService<IVivaExamRoomService>();
    }

    [BindProperty]
    public string? SessionIdInput { get; set; }

    [BindProperty]
    public string ExamIdInput { get; set; } = string.Empty;

    public List<ExamRoomSummaryDto> AvailableRooms { get; set; } = new();

    public string? ErrorMessage { get; set; }

    public bool IsOrchestratorAvailable => _orchestrator != null;

    public async Task OnGetAsync(CancellationToken ct = default)
    {
        if (_examRoomService != null)
        {
            try
            {
                AvailableRooms = await _examRoomService.GetAvailableExamRoomsAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load available exam rooms on Viva index page.");
            }
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

    public async Task<IActionResult> OnPostJoinByCodeAsync(CancellationToken ct = default)
    {
        if (_orchestrator == null)
        {
            ErrorMessage = "Chức năng thi vấn đáp chưa được kích hoạt.";
            return Page();
        }

        var studentId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(studentId))
        {
            return Challenge();
        }

        var email = User.FindFirst(ClaimTypes.Email)?.Value;
        if (!string.IsNullOrWhiteSpace(email))
        {
            if (email.Contains("sv02", StringComparison.OrdinalIgnoreCase) || email.Contains("student2", StringComparison.OrdinalIgnoreCase))
                studentId = "SV02";
            else if (email.Contains("sv03", StringComparison.OrdinalIgnoreCase) || email.Contains("student3", StringComparison.OrdinalIgnoreCase))
                studentId = "SV03";
            else if (email.Contains("sv01", StringComparison.OrdinalIgnoreCase) || email.Contains("student", StringComparison.OrdinalIgnoreCase) || studentId == "1")
                studentId = "SV01";
        }
        else if (studentId == "1")
        {
            studentId = "SV01";
        }

        if (string.IsNullOrWhiteSpace(ExamIdInput))
        {
            ErrorMessage = "Vui lòng nhập mã phòng thi do giảng viên cung cấp.";
            await ReloadRoomsAsync(ct);
            return Page();
        }

        var examCode = ExamIdInput.Trim().ToUpperInvariant();

        // Validate that exam room exists in DB
        if (_examRoomService != null)
        {
            var exists = await _examRoomService.ValidateExamRoomCodeAsync(examCode, ct);
            if (!exists)
            {
                ErrorMessage = $"Mã phòng thi '{examCode}' không tồn tại hoặc chưa có câu hỏi. Vui lòng kiểm tra lại với giảng viên của bạn.";
                await ReloadRoomsAsync(ct);
                return Page();
            }
        }

        try
        {
            var result = await _orchestrator.InitializeSessionAsync(examCode, studentId, ct);
            _logger.LogInformation("Student {StudentId} joined exam room {ExamId} with session {SessionId}",
                studentId, examCode, result.SessionId);
            return RedirectToPage("/Viva/Exam", new { sessionId = result.SessionId });
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid argument when initializing viva session for exam {ExamId}", examCode);
            ErrorMessage = ex.Message;
            await ReloadRoomsAsync(ct);
            return Page();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error initializing viva session for exam {ExamId}", examCode);
            ErrorMessage = "Đã xảy ra lỗi khi khởi tạo phiên thi vấn đáp. Vui lòng thử lại sau.";
            await ReloadRoomsAsync(ct);
            return Page();
        }
    }

    public Task<IActionResult> OnPostCreateAsync(CancellationToken ct = default)
    {
        if (_orchestrator == null)
        {
            ErrorMessage = "Chức năng thi vấn đáp chưa được bật.";
            return Task.FromResult<IActionResult>(Page());
        }

        var studentId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(studentId))
        {
            return Task.FromResult<IActionResult>(Challenge());
        }

        return OnPostJoinByCodeAsync(ct);
    }

    private async Task ReloadRoomsAsync(CancellationToken ct)
    {
        if (_examRoomService != null)
        {
            try
            {
                AvailableRooms = await _examRoomService.GetAvailableExamRoomsAsync(ct);
            }
            catch
            {
                // Ignore reload error
            }
        }
    }
}
