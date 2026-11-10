using System.Text.Json;
using AIVES.BLL.Interfaces.ExamReporting;
using AIVES.BLL.Models.ExamReporting;
using AIVES.BLL.Models.VivaInterview;
using AIVES.BLL.Services.ExamReporting;
using AIVES.DAL.Entities;
using AIVES.DAL.Repositories.Interfaces;
using CriterionOutcomeDto = AIVES.BLL.Models.Grading.CriterionOutcomeDto;

namespace AIVES.BLL.Repositories.ExamReporting;

/// <summary>
/// Real Exam Report repository backed by SQL Server via IVivaSessionRepository, IVivaQuestionRepository,
/// ISystemAccountRepository, and IVivaGradingRepository.
/// Reads persisted VivaGradingResults (AI/Teacher finalized scores) when available.
/// Falls back to <see cref="IRubricGradingService"/> keyword evaluation if not yet persisted (for backwards compatibility).
/// </summary>
public sealed class ExamReportRepository : IExamReportRepository
{
    /// <summary>Ngưỡng đạt duy nhất dùng cho ExamSettings và chấm Rubric (DEC-03).</summary>
    public const decimal DefaultPassThresholdScore = 6.0m;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IVivaSessionRepository _sessionRepo;
    private readonly IVivaQuestionRepository _questionRepo;
    private readonly ISystemAccountRepository _accountRepo;
    private readonly IRubricGradingService _gradingService;
    private readonly IVivaGradingRepository? _gradingRepo;

    public ExamReportRepository(
        IVivaSessionRepository sessionRepo,
        IVivaQuestionRepository questionRepo,
        ISystemAccountRepository accountRepo,
        IRubricGradingService? gradingService = null,
        IVivaGradingRepository? gradingRepo = null)
    {
        _sessionRepo = sessionRepo ?? throw new ArgumentNullException(nameof(sessionRepo));
        _questionRepo = questionRepo ?? throw new ArgumentNullException(nameof(questionRepo));
        _accountRepo = accountRepo ?? throw new ArgumentNullException(nameof(accountRepo));
        _gradingService = gradingService ?? new RubricGradingService();
        _gradingRepo = gradingRepo;
    }

    public async Task<List<AttemptSnapshotDto>> GetStudentAttemptsAsync(string studentId, string examId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(studentId) || string.IsNullOrWhiteSpace(examId))
        {
            return new List<AttemptSnapshotDto>();
        }

        var sessions = await _sessionRepo.GetSessionsWithDetailsAsync(examId: examId, studentId: studentId, ct);
        if (sessions.Count == 0)
        {
            return new List<AttemptSnapshotDto>();
        }

        var questions = await _questionRepo.GetQuestionsByExamAsync(examId, ct);
        var questionMap = questions.ToDictionary(q => q.QuestionId, q => q, StringComparer.OrdinalIgnoreCase);
        var studentName = await ResolveStudentNameAsync(studentId, ct);
        var gradingMap = await LoadGradingResultsMapAsync(sessions, examId, ct);

        return MapSessionsToAttempts(sessions, questionMap, studentId, studentName, gradingMap);
    }

    public async Task<List<AttemptSnapshotDto>> GetClassAttemptsAsync(string classId, string examId, CancellationToken ct = default)
    {
        var effectiveExamId = !string.IsNullOrWhiteSpace(examId) ? examId : classId;
        if (string.IsNullOrWhiteSpace(effectiveExamId))
        {
            return new List<AttemptSnapshotDto>();
        }

        var sessions = await _sessionRepo.GetSessionsWithDetailsAsync(examId: effectiveExamId, studentId: null, ct);
        if (sessions.Count == 0)
        {
            return new List<AttemptSnapshotDto>();
        }

        var questions = await _questionRepo.GetQuestionsByExamAsync(effectiveExamId, ct);
        var questionMap = questions.ToDictionary(q => q.QuestionId, q => q, StringComparer.OrdinalIgnoreCase);

        var accounts = await _accountRepo.SearchAsync(includeDeleted: true, ct: ct);
        var accountNameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var acc in accounts)
        {
            accountNameMap[acc.AccountId.ToString()] = acc.AccountName;
            if (!string.IsNullOrWhiteSpace(acc.AccountEmail))
            {
                accountNameMap[acc.AccountEmail] = acc.AccountName;
            }
        }

        var gradingMap = await LoadGradingResultsMapAsync(sessions, effectiveExamId, ct);

        var result = new List<AttemptSnapshotDto>();
        foreach (var studentGroup in sessions.GroupBy(s => s.StudentId, StringComparer.OrdinalIgnoreCase))
        {
            var sid = studentGroup.Key;
            var sname = accountNameMap.TryGetValue(sid, out var resolved) && !string.IsNullOrWhiteSpace(resolved)
                ? resolved
                : sid;

            var ordered = studentGroup.OrderBy(s => s.StartedAtUtc).ToList();
            var mapped = MapSessionsToAttempts(ordered, questionMap, sid, sname, gradingMap, classOverride: string.IsNullOrWhiteSpace(classId) ? effectiveExamId : classId);
            result.AddRange(mapped);
        }

        return result;
    }

    /// <summary>
    /// Assignment 2 Scope Limitation: The database schema does not include a Class or TeacherAssignment table
    /// (sessions are grouped by <c>ExamId</c>). Therefore, any authenticated teacher/admin with a non-empty
    /// <paramref name="teacherId"/> and <paramref name="classId"/> is permitted to view the exam report.
    /// Role-level access control (<c>Admin,Lecturer</c>) is enforced at the Razor Page boundary.
    /// </summary>
    public Task<bool> IsTeacherAssignedToClassAsync(string teacherId, string classId, CancellationToken ct = default)
    {
        // Intentional ASM2 limitation: no teacher-class assignment table exists in SQL Server.
        return Task.FromResult(!string.IsNullOrWhiteSpace(teacherId) && !string.IsNullOrWhiteSpace(classId));
    }

    public async Task<ExamSettingsSnapshot?> GetExamSettingsAsync(string examId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(examId))
        {
            return null;
        }

        var questions = await _questionRepo.GetQuestionsByExamAsync(examId, ct);
        if (questions.Count == 0)
        {
            var sessionExamIds = await _sessionRepo.GetDistinctExamIdsAsync(ct);
            if (!sessionExamIds.Any(id => string.Equals(id, examId, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }
        }

        var firstTitle = questions.FirstOrDefault()?.Title;
        var examTitle = !string.IsNullOrWhiteSpace(firstTitle)
            ? $"Đề thi {examId} — {firstTitle}"
            : $"Đề thi vấn đáp {examId}";

        return new ExamSettingsSnapshot
        {
            ExamId = examId,
            ExamTitle = examTitle,
            SubjectCode = examId,
            SettingsVersion = "1.0",
            EvaluationMode = ExamEvaluationMode.Score,
            MaxAllowedAttempts = 3,
            PassThresholdScore = DefaultPassThresholdScore,
            MinPassPercentage = 60.0m
        };
    }

    public async Task<List<string>> GetAvailableClassesForTeacherAsync(string teacherId, CancellationToken ct = default)
    {
        var exams = await GetAvailableExamsAsync(ct);
        return exams.Select(e => e.ExamId).ToList();
    }

    public async Task<List<ExamSettingsSnapshot>> GetAvailableExamsAsync(CancellationToken ct = default)
    {
        var examIdsFromQuestions = await _questionRepo.GetDistinctExamIdsAsync(ct);
        var examIdsFromSessions = await _sessionRepo.GetDistinctExamIdsAsync(ct);

        var distinctExamIds = examIdsFromQuestions
            .Concat(examIdsFromSessions)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var snapshots = new List<ExamSettingsSnapshot>();
        foreach (var id in distinctExamIds)
        {
            var settings = await GetExamSettingsAsync(id, ct);
            if (settings != null)
            {
                snapshots.Add(settings);
            }
        }

        return snapshots;
    }

    private async Task<string> ResolveStudentNameAsync(string studentId, CancellationToken ct)
    {
        if (int.TryParse(studentId, out var numericId))
        {
            var acc = await _accountRepo.GetByIdAsync(numericId, includeDeleted: true, ct: ct);
            if (acc != null && !string.IsNullOrWhiteSpace(acc.AccountName))
            {
                return acc.AccountName;
            }
        }

        var byEmail = await _accountRepo.GetByEmailAsync(studentId, ct);
        if (byEmail != null && !string.IsNullOrWhiteSpace(byEmail.AccountName))
        {
            return byEmail.AccountName;
        }

        return studentId;
    }

    private async Task<Dictionary<(Guid SessionId, string QuestionId), VivaGradingResult>> LoadGradingResultsMapAsync(
        List<VivaSession> sessions,
        string examId,
        CancellationToken ct)
    {
        var map = new Dictionary<(Guid SessionId, string QuestionId), VivaGradingResult>();
        if (_gradingRepo == null || sessions.Count == 0)
        {
            return map;
        }

        try
        {
            var examResults = await _gradingRepo.GetByExamIdAsync(examId, ct);
            if (examResults != null && examResults.Count > 0)
            {
                foreach (var r in examResults)
                {
                    map[(r.SessionId, r.QuestionId)] = r;
                }
                return map;
            }
        }
        catch
        {
            // If GetByExamIdAsync is not implemented or throws, fall back to per-session lookup
        }

        foreach (var session in sessions)
        {
            try
            {
                var sessionResults = await _gradingRepo.GetBySessionIdAsync(session.SessionId, ct);
                if (sessionResults != null)
                {
                    foreach (var r in sessionResults)
                    {
                        map[(r.SessionId, r.QuestionId)] = r;
                    }
                }
            }
            catch
            {
                // Continue with next session
            }
        }

        return map;
    }

    private List<AttemptSnapshotDto> MapSessionsToAttempts(
        List<VivaSession> orderedSessions,
        Dictionary<string, VivaQuestion> questionMap,
        string studentId,
        string studentName,
        Dictionary<(Guid SessionId, string QuestionId), VivaGradingResult> gradingMap,
        string? classOverride = null)
    {
        var result = new List<AttemptSnapshotDto>(orderedSessions.Count);
        for (int i = 0; i < orderedSessions.Count; i++)
        {
            var session = orderedSessions[i];
            var state = (VivaSessionState)session.State;

            var examState = state switch
            {
                VivaSessionState.Completed => ExamState.Completed,
                VivaSessionState.TimedOut or VivaSessionState.Failed => ExamState.Abandoned,
                _ => ExamState.InProgress
            };

            var orderedTurns = session.Turns.OrderBy(t => t.TurnOrdinal).ToList();
            var questionResults = new List<QuestionResultDto>();
            var processedQuestions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var qGroup in orderedTurns.GroupBy(t => t.QuestionId, StringComparer.OrdinalIgnoreCase))
            {
                processedQuestions.Add(qGroup.Key);
                var turn1 = qGroup.First();
                var turn2 = qGroup.Skip(1).FirstOrDefault();

                var qTitle = questionMap.TryGetValue(qGroup.Key, out var qEntity)
                    ? (!string.IsNullOrWhiteSpace(qEntity.Content) ? qEntity.Content : qEntity.Title)
                    : $"Câu hỏi {qGroup.Key}";

                var questionResult = new QuestionResultDto
                {
                    QuestionId = qGroup.Key,
                    QuestionTitle = qTitle,
                    CognitiveLevel = _gradingService.GetRubric(qGroup.Key)?.BloomLevel is { Length: > 0 } bloom ? bloom : "Vấn đáp AI",
                    EarnedScore = 0m,
                    MaxScore = 10.0m,
                    PassThresholdScore = DefaultPassThresholdScore,
                    GradingState = examState == ExamState.Abandoned ? ReportGradingState.Failed : ReportGradingState.Pending,
                    IsPassed = false,
                    StudentInitialAnswerTranscript = turn1.TranscriptText ?? string.Empty,
                    AiFollowUpQuestion = turn1.FollowUpQuestionText ?? string.Empty,
                    StudentFollowUpAnswerTranscript = turn2?.TranscriptText ?? string.Empty
                };

                // Nhóm 4: Đọc kết quả đã lưu từ VivaGradingResult nếu có
                if (gradingMap.TryGetValue((session.SessionId, qGroup.Key), out var persistedResult) && persistedResult != null)
                {
                    ApplyPersistedGrading(questionResult, persistedResult, examState);
                }
                else if (examState == ExamState.Completed)
                {
                    // Báo cáo KHÔNG tự chấm. Trước đây chỗ này gọi RubricGradingService để chấm
                    // từ khóa ngay lúc xem báo cáo, khiến điểm hiển thị khác với điểm AI thật và
                    // không có trong VivaGradingResult để giảng viên duyệt. Nay để Pending và chờ
                    // GradingOrchestrator chấm nền; nhãn hiển thị là "Đang chờ chấm".
                    questionResult.GradingState = ReportGradingState.Pending;
                    questionResult.EarnedScore = 0m;
                    questionResult.AiSuggestedScore = 0m;
                    questionResult.IsPassed = false;
                }

                questionResults.Add(questionResult);
            }

            // Bổ sung các câu hỏi có kết quả chấm trong DB nhưng không xuất hiện trong Turns của session
            var additionalPersisted = gradingMap
                .Where(kvp => kvp.Key.SessionId == session.SessionId && !processedQuestions.Contains(kvp.Key.QuestionId))
                .Select(kvp => kvp.Value)
                .OrderBy(r => r.QuestionId)
                .ToList();

            foreach (var persistedResult in additionalPersisted)
            {
                var qTitle = questionMap.TryGetValue(persistedResult.QuestionId, out var qEntity)
                    ? (!string.IsNullOrWhiteSpace(qEntity.Content) ? qEntity.Content : qEntity.Title)
                    : (persistedResult.Question != null ? persistedResult.Question.Title : $"Câu hỏi {persistedResult.QuestionId}");

                var questionResult = new QuestionResultDto
                {
                    QuestionId = persistedResult.QuestionId,
                    QuestionTitle = qTitle,
                    CognitiveLevel = _gradingService.GetRubric(persistedResult.QuestionId)?.BloomLevel is { Length: > 0 } bloom ? bloom : "Vấn đáp AI",
                    EarnedScore = 0m,
                    MaxScore = 10.0m,
                    PassThresholdScore = DefaultPassThresholdScore,
                    GradingState = examState == ExamState.Abandoned ? ReportGradingState.Failed : ReportGradingState.Pending,
                    IsPassed = false
                };

                ApplyPersistedGrading(questionResult, persistedResult, examState);
                questionResults.Add(questionResult);
            }

            var gradingState = examState switch
            {
                ExamState.Abandoned => ReportGradingState.Failed,
                ExamState.Completed when questionResults.Count > 0 && questionResults.All(q => q.GradingState == ReportGradingState.Graded) => ReportGradingState.Graded,
                ExamState.Completed when questionResults.Any(q => q.GradingState == ReportGradingState.Failed) => ReportGradingState.Failed,
                _ => ReportGradingState.Pending
            };

            var attempt = new AttemptSnapshotDto
            {
                AttemptId = session.SessionId.ToString(),
                StudentId = studentId,
                StudentName = studentName,
                ClassId = classOverride ?? session.ExamId,
                ExamId = session.ExamId,
                AttemptOrdinal = i + 1,
                ExamState = examState,
                GradingState = gradingState,
                CompletedAt = session.CompletedAtUtc ?? session.StartedAtUtc,
                QuestionResults = questionResults
            };
            attempt.IsPassed = gradingState == ReportGradingState.Graded && attempt.NormalizedScore >= DefaultPassThresholdScore;

            result.Add(attempt);
        }

        return result;
    }

    private static void ApplyPersistedGrading(
        QuestionResultDto questionResult,
        VivaGradingResult entity,
        ExamState examState)
    {
        // G4: EarnedScore = TeacherFinalScore ?? AiScore ?? 0m
        if (entity.TeacherFinalScore.HasValue)
        {
            questionResult.EarnedScore = entity.TeacherFinalScore.Value;
        }
        else if (entity.AiScore.HasValue)
        {
            questionResult.EarnedScore = entity.AiScore.Value;
        }
        else
        {
            questionResult.EarnedScore = 0m;
        }

        if (entity.TeacherFinalScore.HasValue)
        {
            questionResult.TeacherFinalScore = entity.TeacherFinalScore.Value;
        }

        if (entity.AiScore.HasValue)
        {
            questionResult.AiSuggestedScore = entity.AiScore.Value;
            questionResult.AiScore = entity.AiScore.Value;
        }

        questionResult.TeacherNotes = entity.TeacherNotes ?? string.Empty;
        questionResult.AiFeedback = entity.AiFeedback ?? string.Empty;
        questionResult.Strengths = entity.Strengths ?? string.Empty;
        questionResult.Weaknesses = entity.Weaknesses ?? string.Empty;

        // Trạng thái phê duyệt (Nhóm 4 - HITL)
        questionResult.IsFinalized = entity.GradingState == 2 || entity.TeacherFinalScore.HasValue;
        questionResult.IsAiGraded = !questionResult.IsFinalized && (entity.GradingState == 1 || entity.AiScore.HasValue);

        // Trạng thái chấm của câu
        if (examState == ExamState.Abandoned || entity.GradingState == 3)
        {
            questionResult.GradingState = ReportGradingState.Failed;
            questionResult.IsPassed = false;
        }
        else if (entity.TeacherFinalScore.HasValue || entity.AiScore.HasValue || entity.GradingState == 1 || entity.GradingState == 2)
        {
            questionResult.GradingState = ReportGradingState.Graded;
            questionResult.IsPassed = questionResult.EarnedScore >= questionResult.PassThresholdScore;
        }
        else
        {
            questionResult.GradingState = ReportGradingState.Pending;
            questionResult.IsPassed = false;
        }

        if (!string.IsNullOrWhiteSpace(entity.CriteriaOutcomesJson))
        {
            try
            {
                var criteriaList = JsonSerializer.Deserialize<List<CriterionOutcomeDto>>(entity.CriteriaOutcomesJson, JsonOptions);
                if (criteriaList != null && criteriaList.Count > 0)
                {
                    questionResult.CriteriaOutcomes = criteriaList.ToDictionary(
                        c => !string.IsNullOrWhiteSpace(c.CriterionName) ? c.CriterionName : c.CriterionCode,
                        c => c.IsMet,
                        StringComparer.OrdinalIgnoreCase);

                    // Giữ nguyên điểm đạt / tối đa và giải thích của từng tiêu chí để trang chi tiết
                    // hiển thị được bảng rubric đầy đủ (CriteriaOutcomes chỉ có Đạt/Không đạt).
                    questionResult.RubricDetails = criteriaList.Select(c => new RubricCriterionDetailDto
                    {
                        CriterionCode = c.CriterionCode ?? string.Empty,
                        CriterionName = string.IsNullOrWhiteSpace(c.CriterionName) ? c.CriterionCode ?? string.Empty : c.CriterionName,
                        MaxPoints = c.MaxPoints,
                        AwardedPoints = c.AwardedPoints,
                        IsMet = c.IsMet,
                        Explanation = c.Explanation ?? string.Empty
                    }).ToList();
                }
            }
            catch
            {
                try
                {
                    var dict = JsonSerializer.Deserialize<Dictionary<string, bool>>(entity.CriteriaOutcomesJson, JsonOptions);
                    if (dict != null)
                    {
                        questionResult.CriteriaOutcomes = dict;
                    }
                }
                catch
                {
                    // Ignore malformed json
                }
            }
        }
    }

    private static void ApplyGrading(QuestionResultDto questionResult, RubricGradingResult grading)
    {
        questionResult.EarnedScore = grading.EarnedScore;
        questionResult.MaxScore = grading.MaxScore;
        questionResult.AiSuggestedScore = grading.EarnedScore;
        questionResult.AiScore = grading.EarnedScore;
        questionResult.PassThresholdScore = grading.PassThresholdScore;
        questionResult.GradingState = ReportGradingState.Graded;
        questionResult.IsPassed = grading.IsPassed;
        questionResult.RubricCriteria = grading.RubricDescription;
        questionResult.CriteriaOutcomes = grading.Outcomes.ToDictionary(o => o.Name, o => o.IsMet);
        questionResult.RubricDetails = grading.Outcomes.Select(o => new RubricCriterionDetailDto
        {
            CriterionCode = o.CriterionId,
            CriterionName = o.Name,
            MaxPoints = 0m,          // RubricGradingResult chỉ có tổng điểm, không có thang từng tiêu chí
            AwardedPoints = 0m,
            IsMet = o.IsMet,
            Explanation = o.MatchedKeywords.Count > 0
                ? "Từ khoá khớp: " + string.Join(", ", o.MatchedKeywords)
                : string.Empty
        }).ToList();
        questionResult.Strengths = grading.Strengths;
        questionResult.Weaknesses = grading.Weaknesses;
        questionResult.AiFeedback = grading.Feedback;
        questionResult.IsFinalized = false;
        questionResult.IsAiGraded = true;
    }
}
