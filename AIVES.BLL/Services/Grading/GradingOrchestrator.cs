using System.Text.Json;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Interfaces.ExamReporting;
using AIVES.BLL.Interfaces.Grading;
using AIVES.BLL.Models.ExamReporting;
using AIVES.BLL.Models.Grading;
using AIVES.DAL.Entities;
using AIVES.DAL.Repositories.Interfaces;
using Microsoft.Extensions.Logging;
using GradingState = AIVES.BLL.Models.Grading.GradingState;

namespace AIVES.BLL.Services.Grading;

public sealed class GradingOrchestrator(
    IVivaSessionRepository vivaSessionRepo,
    IVivaQuestionRepository vivaQuestionRepo,
    IVivaGradingRepository vivaGradingRepo,
    IAnswerGradingService aiGradingService,
    IRubricGradingService rubricGradingService,
    ILogger<GradingOrchestrator> logger) : IGradingOrchestrator
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<SessionGradingSummaryDto> GradeSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await vivaSessionRepo.GetWithDetailsAsync(sessionId, ct)
            ?? throw new InvalidOperationException($"Viva session {sessionId} not found.");

        var questions = await vivaQuestionRepo.GetQuestionsByExamAsync(session.ExamId, ct);
        if (questions.Count == 0)
        {
            logger.LogWarning("No questions found for exam {ExamId} in session {SessionId}.", session.ExamId, sessionId);
        }

        foreach (var question in questions)
        {
            try
            {
                // Check if already finalized to avoid overwriting instructor decisions
                var existing = await vivaGradingRepo.GetByQuestionAsync(sessionId, question.QuestionId, ct);
                if (existing != null && existing.GradingState == (byte)GradingState.Finalized)
                {
                    logger.LogInformation("Question {QuestionId} in session {SessionId} is already finalized. Skipping auto-grading.", question.QuestionId, sessionId);
                    continue;
                }

                await GradeQuestionInternalAsync(session, question, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to grade question {QuestionId} in session {SessionId}. Continuing with other questions.", question.QuestionId, sessionId);
            }
        }

        var summary = await GetSessionGradingAsync(sessionId, ct);
        return summary ?? throw new InvalidOperationException($"Failed to load grading summary for session {sessionId}.");
    }

    public async Task<QuestionGradingResultDto> GradeQuestionAsync(Guid sessionId, string questionId, CancellationToken ct = default)
    {
        var session = await vivaSessionRepo.GetWithDetailsAsync(sessionId, ct)
            ?? throw new InvalidOperationException($"Viva session {sessionId} not found.");

        var question = await vivaQuestionRepo.GetByIdAsync(questionId, ct)
            ?? throw new InvalidOperationException($"Question {questionId} not found.");

        var existing = await vivaGradingRepo.GetByQuestionAsync(sessionId, questionId, ct);
        if (existing != null && existing.GradingState == (byte)GradingState.Finalized)
        {
            return MapToDto(existing, question);
        }

        return await GradeQuestionInternalAsync(session, question, ct);
    }

    public async Task<QuestionGradingResultDto> RegradeQuestionAsync(Guid sessionId, string questionId, string requestedBy, CancellationToken ct = default)
    {
        var session = await vivaSessionRepo.GetWithDetailsAsync(sessionId, ct)
            ?? throw new InvalidOperationException($"Viva session {sessionId} not found.");

        var question = await vivaQuestionRepo.GetByIdAsync(questionId, ct)
            ?? throw new InvalidOperationException($"Question {questionId} not found.");

        var existing = await vivaGradingRepo.GetByQuestionAsync(sessionId, questionId, ct);
        if (existing != null && existing.GradingState == (byte)GradingState.Finalized)
        {
            logger.LogWarning("Regrade requested for finalized question {QuestionId} by {User}. Action ignored.", questionId, requestedBy);
            return MapToDto(existing, question);
        }

        return await GradeQuestionInternalAsync(session, question, ct);
    }

    public async Task<QuestionGradingResultDto> FinalizeScoreAsync(FinalizeScoreRequestDto request, string teacherId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        byte[]? expectedRowVersion = null;
        if (!string.IsNullOrWhiteSpace(request.ExpectedRowVersion))
        {
            try
            {
                expectedRowVersion = Convert.FromBase64String(request.ExpectedRowVersion);
            }
            catch (FormatException)
            {
                expectedRowVersion = null;
            }
        }

        var updated = await vivaGradingRepo.FinalizeScoreAsync(
            request.SessionId,
            request.QuestionId,
            request.FinalScore,
            request.TeacherNotes ?? string.Empty,
            teacherId,
            expectedRowVersion,
            request.ChangeReason,
            ct);

        var question = await vivaQuestionRepo.GetByIdAsync(request.QuestionId, ct);
        return MapToDto(updated, question);
    }

    public async Task<SessionGradingSummaryDto?> GetSessionGradingAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await vivaSessionRepo.GetWithDetailsAsync(sessionId, ct);
        if (session is null) return null;

        var questions = await vivaQuestionRepo.GetQuestionsByExamAsync(session.ExamId, ct);
        var persistedResults = await vivaGradingRepo.GetBySessionIdAsync(sessionId, ct);
        var resultMap = persistedResults.ToDictionary(r => r.QuestionId, StringComparer.OrdinalIgnoreCase);

        var questionDtos = new List<QuestionGradingResultDto>();
        foreach (var q in questions)
        {
            var turnsForQ = session.Turns
                .Where(t => string.Equals(t.QuestionId, q.QuestionId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t.TurnOrdinal)
                .ToList();

            var turnDtos = turnsForQ.Select(t => new QuestionTurnDetailDto
            {
                TurnId = t.TurnId,
                TurnOrdinal = t.TurnOrdinal,
                AudioUrl = t.AudioUrl,
                TranscriptText = t.TranscriptText,
                ConfidenceScore = t.ConfidenceScore,
                FollowUpQuestionText = t.FollowUpQuestionText,
                FollowUpAudioUrl = t.FollowUpAudioUrl,
                CreatedAtUtc = t.CreatedAtUtc,
                CompletedAtUtc = t.CompletedAtUtc
            }).ToList();

            var initialAnswer = turnsForQ.FirstOrDefault()?.TranscriptText;

            QuestionGradingResultDto dto;
            if (resultMap.TryGetValue(q.QuestionId, out var existing))
            {
                dto = MapToDto(existing, q);
            }
            else
            {
                dto = new QuestionGradingResultDto
                {
                    SessionId = sessionId,
                    QuestionId = q.QuestionId,
                    QuestionTitle = q.Title,
                    MaxPoints = 10m,
                    State = GradingState.Pending
                };
            }

            dto.QuestionContent = q.Content;
            dto.StudentAnswerTranscript = initialAnswer;
            dto.Turns = turnDtos;

            if (dto.SpeechMetrics == null && turnsForQ.Count > 0)
            {
                dto.SpeechMetrics = CalculateSpeechMetrics(turnsForQ);
            }

            questionDtos.Add(dto);
        }

        var totalMax = questionDtos.Sum(q => q.MaxPoints);
        var totalAi = questionDtos.All(q => q.AiScore.HasValue) ? questionDtos.Sum(q => q.AiScore!.Value) : (decimal?)null;
        var totalFinal = questionDtos.All(q => q.TeacherFinalScore.HasValue) ? questionDtos.Sum(q => q.TeacherFinalScore!.Value) : (decimal?)null;
        var isFullyFinalized = questionDtos.Count > 0 && questionDtos.All(q => q.IsFinalized);

        GradingState overallState;
        if (questionDtos.Any(q => q.State == GradingState.Failed))
        {
            overallState = GradingState.Failed;
        }
        else if (isFullyFinalized)
        {
            overallState = GradingState.Finalized;
        }
        else if (questionDtos.All(q => q.State is GradingState.AiGraded or GradingState.Finalized))
        {
            overallState = GradingState.AiGraded;
        }
        else
        {
            overallState = GradingState.Pending;
        }

        return new SessionGradingSummaryDto
        {
            SessionId = session.SessionId,
            ExamId = session.ExamId,
            StudentId = session.StudentId,
            StudentName = session.StudentId,
            SessionCompletedAtUtc = session.CompletedAtUtc ?? session.StartedAtUtc,
            TotalMaxScore = totalMax,
            TotalAiScore = totalAi,
            TotalFinalScore = totalFinal,
            OverallState = overallState,
            IsFullyFinalized = isFullyFinalized,
            QuestionResults = questionDtos
        };
    }

    public async Task<List<SessionGradingSummaryDto>> GetGradingListByExamAsync(
        string examId,
        string? classId = null,
        GradingState? filterState = null,
        CancellationToken ct = default)
    {
        var sessions = await vivaSessionRepo.GetSessionsWithDetailsAsync(examId, null, ct);
        var summaries = new List<SessionGradingSummaryDto>();

        foreach (var session in sessions)
        {
            var summary = await GetSessionGradingAsync(session.SessionId, ct);
            if (summary is null) continue;

            if (filterState.HasValue && summary.OverallState != filterState.Value)
            {
                continue;
            }

            summaries.Add(summary);
        }

        return summaries;
    }

    private async Task<QuestionGradingResultDto> GradeQuestionInternalAsync(
        VivaSession session,
        VivaQuestion question,
        CancellationToken ct)
    {
        // 1. Collect candidate answer turns
        var questionTurns = session.Turns
            .Where(t => string.Equals(t.QuestionId, question.QuestionId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.TurnOrdinal)
            .ToList();

        var mainTranscript = questionTurns.FirstOrDefault()?.TranscriptText ?? string.Empty;
        var followUpTurns = questionTurns.Skip(1)
            .Where(t => !string.IsNullOrWhiteSpace(t.TranscriptText))
            .Select(t => t.TranscriptText!)
            .ToList();

        // 2. Fetch question rubric
        var rubric = rubricGradingService.GetRubric(question.QuestionId);
        var rubricDtoList = rubric?.Criteria.Select(c => new RubricCriterionDto
        {
            CriterionCode = c.Id,
            CriterionName = c.Name,
            Description = c.Description,
            MaxPoints = rubric.PointsFor(c),
            Weight = c.Weight,
            Keywords = c.Keywords.ToList()
        }).ToList() ?? new List<RubricCriterionDto>();

        var input = new GradingInputDto
        {
            SessionId = session.SessionId,
            QuestionId = question.QuestionId,
            QuestionText = question.Content,
            BloomLevel = rubric?.BloomLevel,
            MaxPoints = rubric?.MaxScore ?? 10.0m,
            StudentAnswerTranscript = mainTranscript,
            FollowUpTurns = followUpTurns,
            RubricCriteria = rubricDtoList
        };

        // 3. Call AI Service with isolated fault tolerance
        QuestionGradingResultDto? gradingResult = null;
        var aiFailed = false;

        try
        {
            gradingResult = await aiGradingService.GradeAnswerAsync(input, ct);
            if (gradingResult == null || !gradingResult.AiScore.HasValue)
            {
                aiFailed = true;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AI grading threw exception for session {SessionId}, question {QuestionId}. Activating keyword fallback.", session.SessionId, question.QuestionId);
            aiFailed = true;
        }

        // 4. Automatic Fallback to keyword-based RubricGradingService
        if (aiFailed)
        {
            gradingResult = ExecuteFallbackGrading(question, input, rubric);
        }

        // 5. Persist result to database
        var entity = new VivaGradingResult
        {
            SessionId = session.SessionId,
            QuestionId = question.QuestionId,
            AiScore = gradingResult!.AiScore,
            AiFeedback = gradingResult.AiFeedback,
            Strengths = gradingResult.Strengths,
            Weaknesses = gradingResult.Weaknesses,
            CriteriaOutcomesJson = JsonSerializer.Serialize(gradingResult.CriteriaOutcomes, JsonOptions),
            AiProvider = gradingResult.AiProvider,
            AiModel = gradingResult.AiModel,
            GradingState = (byte)gradingResult.State
        };

        var saved = await vivaGradingRepo.UpsertAiResultAsync(entity, ct);
        var mapped = MapToDto(saved, question);
        mapped.QuestionContent = question.Content;
        mapped.StudentAnswerTranscript = mainTranscript;
        mapped.SpeechMetrics = gradingResult.SpeechMetrics ?? CalculateSpeechMetrics(questionTurns);
        return mapped;
    }

    private QuestionGradingResultDto ExecuteFallbackGrading(
        VivaQuestion question,
        GradingInputDto input,
        QuestionRubric? rubric)
    {
        var rubricResult = rubricGradingService.Grade(
            question.QuestionId,
            question.Content,
            input.StudentAnswerTranscript,
            passThresholdScore: 5.0m);

        if (rubricResult != null)
        {
            var outcomes = rubricResult.Outcomes.Select(o => new CriterionOutcomeDto
            {
                CriterionCode = o.CriterionId,
                CriterionName = o.Name,
                IsMet = o.IsMet,
                AwardedPoints = o.IsMet
                    ? (rubric != null ? rubric.PointsFor(rubric.Criteria.FirstOrDefault(c => c.Id == o.CriterionId) ?? new RubricCriterion { Weight = 1 }) : 3.0m)
                    : 0m,
                Explanation = o.IsMet ? $"Khớp từ khóa: {string.Join(", ", o.MatchedKeywords)}" : "Chưa đạt tiêu chí từ khóa"
            }).ToList();

            return new QuestionGradingResultDto
            {
                SessionId = input.SessionId,
                QuestionId = question.QuestionId,
                QuestionTitle = question.Title,
                BloomLevel = rubric?.BloomLevel,
                MaxPoints = rubricResult.MaxScore,
                AiScore = rubricResult.EarnedScore,
                AiFeedback = rubricResult.Feedback,
                Strengths = rubricResult.Strengths,
                Weaknesses = rubricResult.Weaknesses,
                AiProvider = "KeywordFallback",
                AiModel = "RubricCatalog",
                State = GradingState.AiGraded,
                CriteriaOutcomes = outcomes
            };
        }

        return new QuestionGradingResultDto
        {
            SessionId = input.SessionId,
            QuestionId = question.QuestionId,
            QuestionTitle = question.Title,
            MaxPoints = 10m,
            AiProvider = "None",
            State = GradingState.Failed
        };
    }

    private static SpeechMetricsDto CalculateSpeechMetrics(List<VivaSessionTurn> turns)
    {
        var allText = string.Join(" ", turns.Select(t => t.TranscriptText ?? string.Empty)).Trim();
        var words = allText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var totalWords = words.Length;

        double totalDurationSec = 0;
        foreach (var turn in turns)
        {
            if (turn.CompletedAtUtc.HasValue && turn.CompletedAtUtc.Value > turn.CreatedAtUtc)
            {
                totalDurationSec += (turn.CompletedAtUtc.Value - turn.CreatedAtUtc).TotalSeconds;
            }
        }
        if (totalDurationSec <= 0)
        {
            totalDurationSec = Math.Max(20.0, totalWords * 0.4);
        }

        var speedWpm = totalDurationSec > 0 ? Math.Round(totalWords / (totalDurationSec / 60.0), 1) : 0;

        var fillerKeywords = new[] { "à", "ừm", "ừ", "kiểu", "thì", "dạ", "như là", "kiểu như", "ơ" };
        var identifiedFillers = new List<string>();
        int fillerCount = 0;

        foreach (var kw in fillerKeywords)
        {
            int count = 0;
            int idx = 0;
            while ((idx = allText.IndexOf(kw, idx, StringComparison.OrdinalIgnoreCase)) != -1)
            {
                count++;
                idx += kw.Length;
            }
            if (count > 0)
            {
                fillerCount += count;
                identifiedFillers.Add($"{kw} ({count})");
            }
        }

        double ratio = totalWords > 0 ? Math.Round((double)fillerCount / totalWords, 3) : 0;

        return new SpeechMetricsDto
        {
            AnswerDurationSeconds = Math.Round(totalDurationSec, 1),
            SpeakingSpeedWpm = speedWpm,
            FillerWordsCount = fillerCount,
            FillerWordsRatio = ratio,
            IdentifiedFillerWords = identifiedFillers
        };
    }

    private static QuestionGradingResultDto MapToDto(VivaGradingResult entity, VivaQuestion? question)
    {
        var criteria = new List<CriterionOutcomeDto>();
        if (!string.IsNullOrWhiteSpace(entity.CriteriaOutcomesJson))
        {
            try
            {
                criteria = JsonSerializer.Deserialize<List<CriterionOutcomeDto>>(entity.CriteriaOutcomesJson, JsonOptions) ?? new();
            }
            catch
            {
                criteria = new();
            }
        }

        var changeLogs = entity.ChangeLogs?
            .OrderByDescending(l => l.ChangedAtUtc)
            .Select(l => new GradeChangeLogDto
            {
                Id = l.Id,
                GradingResultId = l.GradingResultId,
                QuestionId = entity.QuestionId,
                QuestionTitle = question?.Title ?? entity.QuestionId,
                OldScore = l.OldScore,
                NewScore = l.NewScore,
                ChangedBy = l.ChangedBy,
                Reason = l.Reason,
                ChangedAtUtc = l.ChangedAtUtc
            }).ToList() ?? new();

        return new QuestionGradingResultDto
        {
            Id = entity.Id,
            SessionId = entity.SessionId,
            QuestionId = entity.QuestionId,
            QuestionTitle = question?.Title ?? entity.QuestionId,
            QuestionContent = question?.Content,
            MaxPoints = 10m,
            AiScore = entity.AiScore,
            AiFeedback = entity.AiFeedback,
            Strengths = entity.Strengths,
            Weaknesses = entity.Weaknesses,
            CriteriaOutcomes = criteria,
            AiProvider = entity.AiProvider,
            AiModel = entity.AiModel,
            State = (GradingState)entity.GradingState,
            TeacherFinalScore = entity.TeacherFinalScore,
            TeacherNotes = entity.TeacherNotes,
            FinalizedBy = entity.FinalizedBy,
            FinalizedAtUtc = entity.FinalizedAtUtc,
            RowVersion = entity.RowVersion != null ? Convert.ToBase64String(entity.RowVersion) : null,
            ChangeLogs = changeLogs
        };
    }
}
