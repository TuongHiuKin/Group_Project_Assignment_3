using System.Security.Claims;
using AIVES.BLL.Interfaces.VivaInterview;
using AIVES.BLL.Models.VivaInterview;
using AIVES.BLL.Options;
using AIVES.BLL.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace StudentNameMVC.Pages.Viva;

[Authorize]
[IgnoreAntiforgeryToken(Order = 1001)] // Form handlers can use explicit header/body tokens or automatic MVC validation
public class ExamModel : PageModel
{
    private readonly IVivaSessionOrchestrator? _orchestrator;
    private readonly IVivaRealtimeSessionService? _realtimeService;
    private readonly ILogger<ExamModel> _logger;

    public ExamModel(
        ILogger<ExamModel> logger,
        IVivaSessionOrchestrator? orchestrator = null,
        IVivaRealtimeSessionService? realtimeService = null,
        IOptions<AiServiceOptions>? aiOptions = null)
    {
        _logger = logger;
        _orchestrator = orchestrator;
        _realtimeService = realtimeService;
        UseBrowserSpeechSynthesis = aiOptions?.Value.UseBrowserSpeechSynthesis ?? false;
    }

    /// <summary>True when the page reads questions with the browser speechSynthesis API instead of AI audio files.</summary>
    public bool UseBrowserSpeechSynthesis { get; }

    [BindProperty(SupportsGet = true)]
    public Guid? SessionId { get; set; }

    public string ExamId { get; set; } = string.Empty;
    public string StudentId { get; set; } = string.Empty;
    public string CandidateName { get; set; } = string.Empty;
    public string CandidateRole { get; set; } = string.Empty;
    public VivaSessionState SessionState { get; set; } = VivaSessionState.Created;

    public string InitialQuestionId { get; set; } = string.Empty;
    public string InitialQuestionTitle { get; set; } = string.Empty;
    public string InitialQuestionContent { get; set; } = string.Empty;
    public string? InitialQuestionAudioUrl { get; set; }
    public int InitialTimeLimitSeconds { get; set; } = 90;
    public int CurrentTurn { get; set; } = 0;
    public int MaxTurns { get; set; } = 2;
    public string? InitialTranscript { get; set; }
    public decimal? InitialTranscriptConfidence { get; set; }
    public string? InitialFollowUpQuestionId { get; set; }
    public string? InitialFollowUpContent { get; set; }
    public int InitialFollowUpOrdinal { get; set; }
    public string? InitialFollowUpAudioUrl { get; set; }
    public int? InitialSecondsRemaining { get; set; }

    public string? ErrorMessage { get; set; }
    public bool IsOrchestratorAvailable => _orchestrator != null;

    public async Task<IActionResult> OnGetAsync(Guid? sessionId, CancellationToken ct = default)
    {
        if (sessionId.HasValue && sessionId.Value != Guid.Empty)
        {
            SessionId = sessionId.Value;
        }

        if (!SessionId.HasValue || SessionId.Value == Guid.Empty)
        {
            return RedirectToPage("/Viva/Index");
        }

        StudentId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.Identity?.Name ?? string.Empty;
        CandidateName = User.Identity?.Name ?? StudentId;
        CandidateRole = User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Role)?.Value switch
        {
            ApplicationRoles.Admin => "Quản trị viên",
            ApplicationRoles.Lecturer => "Giảng viên",
            ApplicationRoles.Staff => "Sinh viên",
            _ => "Thí sinh"
        };

        if (_orchestrator != null)
        {
            try
            {
                var isAuthorized = await _orchestrator.AuthorizeUserForSessionAsync(SessionId.Value, StudentId, ct);
                if (!isAuthorized)
                {
                    _logger.LogWarning("User {UserId} unauthorized for Viva session {SessionId}", StudentId, SessionId.Value);
                    return RedirectToPage("/Account/AccessDenied");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not verify session authorization for {SessionId}", SessionId.Value);
            }
        }

        if (_realtimeService != null)
        {
            try
            {
                var snapshot = await _realtimeService.GetSnapshotAsync(SessionId.Value, ct);
                if (snapshot != null)
                {
                    ExamId = snapshot.Status.ExamId;
                    StudentId = snapshot.Status.StudentId;
                    SessionState = snapshot.Status.State;
                    CurrentTurn = snapshot.Status.CurrentTurnOrdinal;
                    MaxTurns = snapshot.Status.MaxTurnsAllowed;

                    if (snapshot.CurrentQuestion != null)
                    {
                        InitialQuestionId = snapshot.CurrentQuestion.QuestionId;
                        InitialQuestionTitle = snapshot.CurrentQuestion.Title;
                        InitialQuestionContent = snapshot.CurrentQuestion.Content;
                        InitialQuestionAudioUrl = snapshot.CurrentQuestion.AudioUrl;
                        InitialTimeLimitSeconds = snapshot.CurrentQuestion.TimeLimitSeconds;
                    }

                    if (snapshot.LatestTranscript != null)
                    {
                        InitialTranscript = snapshot.LatestTranscript.TranscriptText;
                        InitialTranscriptConfidence = snapshot.LatestTranscript.ConfidenceScore;
                    }

                    if (snapshot.CurrentFollowUp != null)
                    {
                        InitialFollowUpQuestionId = snapshot.CurrentFollowUp.FollowUpQuestionId;
                        InitialFollowUpContent = snapshot.CurrentFollowUp.FollowUpContent;
                        InitialFollowUpOrdinal = snapshot.CurrentFollowUp.FollowUpOrdinal;
                        InitialFollowUpAudioUrl = snapshot.CurrentFollowUp.AudioUrl;
                    }

                    if (snapshot.Timer != null)
                    {
                        InitialSecondsRemaining = snapshot.Timer.SecondsRemaining;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load session snapshot for {SessionId}", SessionId.Value);
            }
        }

        return Page();
    }

    public async Task<IActionResult> OnPostStartSessionAsync([FromForm] Guid sessionId, CancellationToken ct = default)
    {
        if (sessionId == Guid.Empty)
        {
            return BadRequest(new { message = "Mã phiên thi không hợp lệ." });
        }

        if (_orchestrator == null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                message = "Hệ thống Viva Interview Orchestrator chưa được kích hoạt."
            });
        }

        var studentId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.Identity?.Name ?? string.Empty;
        var isAuthorized = await _orchestrator.AuthorizeUserForSessionAsync(sessionId, studentId, ct);
        if (!isAuthorized)
        {
            return Forbid();
        }

        try
        {
            var question = await _orchestrator.StartSessionAsync(sessionId, ct);
            return new JsonResult(new { success = true, question });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "StartSession rejected for {SessionId}", sessionId);
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error starting session {SessionId}", sessionId);
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                message = "Đã xảy ra lỗi máy chủ khi bắt đầu bài thi."
            });
        }
    }

    public async Task<IActionResult> OnPostSubmitAudioAsync(
        [FromForm] Guid sessionId,
        [FromForm] string questionId,
        IFormFile? audioFile,
        [FromForm] int durationSeconds,
        CancellationToken ct = default)
    {
        if (sessionId == Guid.Empty)
        {
            return BadRequest(new { message = "Mã phiên thi không hợp lệ." });
        }

        if (string.IsNullOrWhiteSpace(questionId))
        {
            return BadRequest(new { message = "Mã câu hỏi không được để trống." });
        }

        if (audioFile == null || audioFile.Length == 0)
        {
            return BadRequest(new { message = "Không tìm thấy dữ liệu tệp âm thanh câu trả lời." });
        }

        const long maxSizeBytes = 25 * 1024 * 1024; // 25MB
        if (audioFile.Length > maxSizeBytes)
        {
            return BadRequest(new { message = "Tệp âm thanh vượt quá giới hạn tối đa cho phép (25MB)." });
        }

        if (_orchestrator == null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                message = "Hệ thống Viva Interview Orchestrator chưa được kích hoạt."
            });
        }

        var studentId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.Identity?.Name ?? string.Empty;
        var isAuthorized = await _orchestrator.AuthorizeUserForSessionAsync(sessionId, studentId, ct);
        if (!isAuthorized)
        {
            return Forbid();
        }

        try
        {
            using var ms = new MemoryStream();
            await audioFile.CopyToAsync(ms, ct);
            var audioBytes = ms.ToArray();
            var contentType = string.IsNullOrWhiteSpace(audioFile.ContentType) ? "audio/webm" : audioFile.ContentType;

            var submission = new VivaAnswerSubmissionDto(
                SessionId: sessionId,
                QuestionId: questionId,
                AudioData: audioBytes,
                ContentType: contentType,
                DurationSeconds: Math.Max(1, durationSeconds)
            );

            var transcript = await _orchestrator.SubmitAnswerAudioAsync(submission, ct);

            try
            {
                // Tạo câu hỏi phụ cho lượt vừa nộp; khi câu hỏi đủ lượt, orchestrator chuyển câu tiếp theo hoặc hoàn thành phiên.
                await _orchestrator.GenerateFollowUpAsync(sessionId, transcript.TurnId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Follow-up generation failed for session {SessionId}, turn {TurnId}",
                    sessionId, transcript.TurnId);
            }

            return new JsonResult(new
            {
                success = true,
                turnId = transcript.TurnId,
                transcriptText = transcript.TranscriptText,
                confidenceScore = transcript.ConfidenceScore,
                completedAtUtc = transcript.CompletedAtUtc
            });
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Validation error submitting audio for session {SessionId}", sessionId);
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Invalid state submitting audio for session {SessionId}", sessionId);
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error submitting audio for session {SessionId}", sessionId);
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                message = "Lỗi trong quá trình xử lý câu trả lời âm thanh: " + ex.Message
            });
        }
    }

    /// <summary>Thử tạo lại câu hỏi phụ sau khi LLM lỗi (phiên đang ở TranscriptReady).</summary>
    public async Task<IActionResult> OnPostRetryFollowUpAsync([FromForm] Guid sessionId, [FromForm] Guid turnId, CancellationToken ct = default)
    {
        var guard = await EnsureTranscriptReadyAsync(sessionId, ct);
        if (guard != null) return guard;
        if (turnId == Guid.Empty)
        {
            return BadRequest(new { message = "Mã lượt trả lời không hợp lệ." });
        }

        try
        {
            var followUp = await _orchestrator!.GenerateFollowUpAsync(sessionId, turnId, ct);
            return new JsonResult(new { success = true, followUpGenerated = followUp != null });
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "RetryFollowUp rejected for {SessionId}", sessionId);
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>Kết thúc bài thi khi sinh viên không muốn chờ câu hỏi phụ nữa (phiên đang ở TranscriptReady).</summary>
    public async Task<IActionResult> OnPostFinishSessionAsync([FromForm] Guid sessionId, CancellationToken ct = default)
    {
        var guard = await EnsureTranscriptReadyAsync(sessionId, ct);
        if (guard != null) return guard;

        try
        {
            await _orchestrator!.CompleteSessionAsync(sessionId, ct);
            return new JsonResult(new { success = true });
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "FinishSession rejected for {SessionId}", sessionId);
            return BadRequest(new { message = ex.Message });
        }
    }

    private async Task<IActionResult?> EnsureTranscriptReadyAsync(Guid sessionId, CancellationToken ct)
    {
        if (sessionId == Guid.Empty)
        {
            return BadRequest(new { message = "Mã phiên thi không hợp lệ." });
        }

        if (_orchestrator == null || _realtimeService == null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                message = "Hệ thống Viva Interview Orchestrator chưa được kích hoạt."
            });
        }

        var studentId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.Identity?.Name ?? string.Empty;
        if (!await _orchestrator.AuthorizeUserForSessionAsync(sessionId, studentId, ct))
        {
            return Forbid();
        }

        var snapshot = await _realtimeService.GetSnapshotAsync(sessionId, ct);
        if (snapshot?.Status.State != VivaSessionState.TranscriptReady)
        {
            return BadRequest(new { message = "Phiên thi không ở trạng thái chờ câu hỏi phụ." });
        }

        return null;
    }
}
