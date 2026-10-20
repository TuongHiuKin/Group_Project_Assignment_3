using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AIVES.BLL.Interfaces.VivaInterview;
using AIVES.BLL.Models.VivaInterview;
using AIVES.DAL.Entities;
using AIVES.DAL.Repositories.Interfaces;

namespace AIVES.BLL.Services.VivaInterview;

public partial class VivaExamRoomService : IVivaExamRoomService
{
    private static readonly Regex SafeCodeRegex = SafeCodeRegexCompiled();
    private readonly IVivaQuestionRepository _questionRepo;
    private readonly ILogger<VivaExamRoomService> _logger;

    public VivaExamRoomService(
        IVivaQuestionRepository questionRepo,
        ILogger<VivaExamRoomService> logger)
    {
        _questionRepo = questionRepo ?? throw new ArgumentNullException(nameof(questionRepo));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<string> CreateExamRoomAsync(CreateExamRoomDto request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string examCode;
        if (string.IsNullOrWhiteSpace(request.ExamId))
        {
            examCode = $"VIVA-{Guid.NewGuid().ToString("N")[..4].ToUpperInvariant()}";
        }
        else
        {
            examCode = request.ExamId.Trim().ToUpperInvariant();
        }

        if (examCode.Length is < 2 or > 40 || !SafeCodeRegex.IsMatch(examCode))
        {
            throw new ArgumentException(
                "Mã phòng thi không hợp lệ. Mã chỉ được chứa chữ cái, chữ số, dấu gạch nối và tối đa 40 ký tự.",
                nameof(request));
        }

        var exists = await _questionRepo.ExamExistsAsync(examCode, ct);
        if (exists)
        {
            throw new InvalidOperationException($"Mã phòng thi '{examCode}' đã tồn tại trong hệ thống. Vui lòng chọn một mã khác.");
        }

        if (request.Questions == null || request.Questions.Count == 0)
        {
            throw new ArgumentException("Bài thi phải có ít nhất một câu hỏi vấn đáp.", nameof(request));
        }

        var defaultTime = request.DefaultTimeLimitSeconds > 0 ? request.DefaultTimeLimitSeconds : 90;
        var entities = new List<VivaQuestion>();

        for (var i = 0; i < request.Questions.Count; i++)
        {
            var q = request.Questions[i];
            if (string.IsNullOrWhiteSpace(q.Title))
            {
                throw new ArgumentException($"Câu hỏi số {i + 1} phải có tiêu đề câu hỏi.", nameof(request));
            }

            if (string.IsNullOrWhiteSpace(q.Content))
            {
                throw new ArgumentException($"Câu hỏi số {i + 1} phải có nội dung câu hỏi chi tiết.", nameof(request));
            }

            entities.Add(new VivaQuestion
            {
                QuestionId = $"{examCode}-Q{(i + 1):D2}",
                ExamId = examCode,
                Title = q.Title.Trim(),
                Content = q.Content.Trim(),
                TimeLimitSeconds = q.TimeLimitSeconds > 0 ? q.TimeLimitSeconds : defaultTime,
                AudioUrl = null
            });
        }

        _logger.LogInformation("Creating new Viva exam room {ExamId} with {Count} questions", examCode, entities.Count);
        await _questionRepo.AddQuestionsAsync(entities, ct);

        return examCode;
    }

    /// <inheritdoc />
    public async Task<bool> ValidateExamRoomCodeAsync(string examId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(examId))
        {
            return false;
        }

        var normalized = examId.Trim().ToUpperInvariant();
        return await _questionRepo.ExamExistsAsync(normalized, ct);
    }

    /// <inheritdoc />
    public async Task<List<ExamRoomSummaryDto>> GetAvailableExamRoomsAsync(CancellationToken ct = default)
    {
        var questions = await _questionRepo.GetAllQuestionsAsync(ct);
        if (questions == null || questions.Count == 0)
        {
            return [];
        }

        var groups = questions.GroupBy(q => q.ExamId, StringComparer.OrdinalIgnoreCase);
        var result = new List<ExamRoomSummaryDto>();

        foreach (var group in groups)
        {
            var first = group.First();
            var inferredTitle = !string.IsNullOrWhiteSpace(first.Title) ? first.Title : group.Key;
            result.Add(new ExamRoomSummaryDto(
                ExamId: group.Key,
                Title: inferredTitle,
                QuestionCount: group.Count(),
                DefaultTimeLimitSeconds: first.TimeLimitSeconds,
                QuestionTitles: group.Select(q => q.Title).ToList()
            ));
        }

        return result.OrderByDescending(r => r.ExamId).ToList();
    }

    /// <inheritdoc />
    public async Task<ExamRoomDetailDto?> GetExamRoomDetailsAsync(string examId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(examId))
        {
            return null;
        }

        var normalized = examId.Trim().ToUpperInvariant();
        var questions = await _questionRepo.GetQuestionsByExamAsync(normalized, ct);
        if (questions == null || questions.Count == 0)
        {
            return null;
        }

        var first = questions.First();
        return new ExamRoomDetailDto(
            ExamId: normalized,
            Title: first.Title,
            QuestionCount: questions.Count,
            DefaultTimeLimitSeconds: first.TimeLimitSeconds,
            Questions: questions.Select(q => new CreateExamQuestionDto(q.Title, q.Content, q.TimeLimitSeconds)).ToList()
        );
    }

    [GeneratedRegex(@"^[A-Z0-9_\-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeCodeRegexCompiled();
}
