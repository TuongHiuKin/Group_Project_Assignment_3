using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using AIVES.BLL.Interfaces.VivaInterview;
using AIVES.BLL.Models.VivaInterview;
using AIVES.BLL.Security;

namespace StudentNameMVC.Pages.Viva;

[Authorize(Roles = $"{ApplicationRoles.Lecturer},{ApplicationRoles.Admin}")]
public class CreateExamModel : PageModel
{
    private readonly IVivaExamRoomService _examRoomService;
    private readonly ILogger<CreateExamModel> _logger;

    public CreateExamModel(
        IVivaExamRoomService examRoomService,
        ILogger<CreateExamModel> logger)
    {
        _examRoomService = examRoomService ?? throw new ArgumentNullException(nameof(examRoomService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [BindProperty]
    public string ExamId { get; set; } = string.Empty;

    [BindProperty]
    public string ExamTitle { get; set; } = string.Empty;

    [BindProperty]
    public int DefaultTimeLimitSeconds { get; set; } = 90;

    [BindProperty]
    public List<string> QuestionTitles { get; set; } = new();

    [BindProperty]
    public List<string> QuestionContents { get; set; } = new();

    [BindProperty]
    public List<int> QuestionTimes { get; set; } = new();

    public List<ExamRoomSummaryDto> ExistingRooms { get; set; } = new();

    public string? ErrorMessage { get; set; }
    public string? CreatedRoomCode { get; set; }

    public async Task OnGetAsync(CancellationToken ct = default)
    {
        CreatedRoomCode = TempData["CreatedExamCode"] as string;
        ExamId = $"VIVA-{new Random().Next(1000, 9999)}";
        try
        {
            ExistingRooms = await _examRoomService.GetAvailableExamRoomsAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load existing exam rooms in OnGet.");
            ExistingRooms = new();
        }
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct = default)
    {
        try
        {
            ExistingRooms = await _examRoomService.GetAvailableExamRoomsAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load existing exam rooms in OnPost.");
            ExistingRooms = new();
        }

        if (string.IsNullOrWhiteSpace(ExamId))
        {
            ErrorMessage = "Vui lòng nhập mã phòng thi / mã đề thi.";
            return Page();
        }

        var questions = new List<CreateExamQuestionDto>();
        for (var i = 0; i < QuestionTitles.Count; i++)
        {
            var title = QuestionTitles[i];
            var content = i < QuestionContents.Count ? QuestionContents[i] : string.Empty;
            var time = i < QuestionTimes.Count && QuestionTimes[i] > 0 ? QuestionTimes[i] : DefaultTimeLimitSeconds;

            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(content))
            {
                continue; // Skip empty rows
            }

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(content))
            {
                ErrorMessage = $"Câu hỏi số {i + 1} phải nhập đầy đủ cả Tiêu đề và Nội dung câu hỏi.";
                return Page();
            }

            questions.Add(new CreateExamQuestionDto(title.Trim(), content.Trim(), time));
        }

        if (questions.Count == 0)
        {
            ErrorMessage = "Vui lòng thêm ít nhất một câu hỏi vấn đáp cho phòng thi.";
            return Page();
        }

        try
        {
            var dto = new CreateExamRoomDto(
                ExamId: ExamId.Trim().ToUpperInvariant(),
                Title: string.IsNullOrWhiteSpace(ExamTitle) ? ExamId.Trim() : ExamTitle.Trim(),
                DefaultTimeLimitSeconds: DefaultTimeLimitSeconds > 0 ? DefaultTimeLimitSeconds : 90,
                Questions: questions
            );

            var createdCode = await _examRoomService.CreateExamRoomAsync(dto, ct);
            _logger.LogInformation("Lecturer {Lecturer} created exam room {RoomCode} with {Count} questions",
                User.Identity?.Name ?? "Unknown", createdCode, questions.Count);

            TempData["CreatedExamCode"] = createdCode;
            TempData["SuccessMessage"] = $"Khởi tạo phòng thi thành công! Mã phòng thi: {createdCode}. Hãy gửi mã này cho thí sinh.";
            return RedirectToPage();
        }
        catch (ArgumentException ex)
        {
            ErrorMessage = ex.Message;
            return Page();
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
            return Page();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating exam room {ExamId}", ExamId);
            ErrorMessage = "Đã xảy ra lỗi khi tạo phòng thi. Vui lòng kiểm tra lại thông tin và thử lại.";
            return Page();
        }
    }
}
