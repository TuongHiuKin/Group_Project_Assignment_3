namespace AIVES.BLL.Models.ExamReporting;

public enum ExamEvaluationMode
{
    Score = 1,
    PassFail = 2
}

public enum ExamState
{
    InProgress = 1,
    Completed = 2,
    Abandoned = 3,
    Cancelled = 4
}

/// <summary>
/// Trạng thái chấm điểm dùng riêng cho BÁO CÁO (Nhóm 6). Tách khỏi
/// <see cref="Grading.GradingState"/> của Nhóm 4 (Pending/AiGraded/Finalized/Failed) để hai enum
/// không trùng tên và không ai nhầm giá trị byte. Giá trị ở đây là của DTO báo cáo,
/// KHÔNG phải giá trị lưu trong cột VivaGradingResult.GradingState.
/// </summary>
public enum ReportGradingState
{
    Pending = 1,
    Graded = 2,
    Failed = 3
}

public class ExamSettingsSnapshot
{
    public string ExamId { get; set; } = string.Empty;
    public string ExamTitle { get; set; } = string.Empty;
    public string SubjectCode { get; set; } = "PRN222";
    public string SettingsVersion { get; set; } = "1.0";
    public ExamEvaluationMode EvaluationMode { get; set; } = ExamEvaluationMode.Score;
    public int MaxAllowedAttempts { get; set; } = 3;
    public decimal PassThresholdScore { get; set; } = 6.0m;
    public decimal MinPassPercentage { get; set; } = 60.0m;
    public List<string> RequiredCriteriaKeys { get; set; } = new();
}

public class QuestionResultDto
{
    public string QuestionId { get; set; } = string.Empty;
    public string QuestionTitle { get; set; } = string.Empty;
    public string CognitiveLevel { get; set; } = "Vận dụng"; // Thang Bloom: Nhớ, Hiểu, Vận dụng, Phân tích (Nhóm 1)
    
    // Điểm số & Chấm điểm (Nhóm 4 - Human-in-the-loop)
    public decimal EarnedScore { get; set; }
    public decimal MaxScore { get; set; } = 10.0m;
    public decimal AiSuggestedScore { get; set; } // Điểm AI gợi ý
    public decimal TeacherFinalScore { get; set; } // Điểm Giảng viên chốt
    public string TeacherNotes { get; set; } = string.Empty;

    public bool IsPassed { get; set; }
    public decimal PassThresholdScore { get; set; } = 6.0m;
    public ReportGradingState GradingState { get; set; } = ReportGradingState.Graded;
    public ExamEvaluationMode EvaluationMode { get; set; } = ExamEvaluationMode.Score;

    // Trạng thái phê duyệt điểm (Nhóm 4 - HITL)
    public bool IsFinalized { get; set; }
    public bool IsAiGraded { get; set; }

    // Lõi phỏng vấn AI & Transcript hội thoại (Nhóm 3)
    public string StudentInitialAnswerTranscript { get; set; } = string.Empty; // STT câu trả lời ban đầu
    public string AiFollowUpQuestion { get; set; } = string.Empty; // Câu hỏi hỏi xoáy / làm rõ của AI
    public string StudentFollowUpAnswerTranscript { get; set; } = string.Empty; // STT câu trả lời hỏi xoáy
    
    // Phản hồi học tập chi tiết của AI (Nhóm 4, Nhóm 6)
    public string AiFeedback { get; set; } = string.Empty;
    public string Strengths { get; set; } = string.Empty; // Điểm mạnh
    public string Weaknesses { get; set; } = string.Empty; // Điểm yếu / ý còn thiếu
    public string RubricCriteria { get; set; } = string.Empty;
    public Dictionary<string, bool> CriteriaOutcomes { get; set; } = new();

    /// <summary>
    /// Chi tiết từng tiêu chí rubric: điểm đạt / điểm tối đa và giải thích của AI.
    /// <see cref="CriteriaOutcomes"/> chỉ giữ Đạt/Không đạt nên không đủ để giảng viên đối chiếu;
    /// danh sách này là nguồn cho bảng rubric ở trang chi tiết bài thi.
    /// </summary>
    public List<RubricCriterionDetailDto> RubricDetails { get; set; } = new();

    /// <summary>Điểm AI đề xuất (nullable: null khi chưa chấm, khác với 0 điểm thật).</summary>
    public decimal? AiScore { get; set; }
}

/// <summary>Một tiêu chí rubric kèm kết quả chấm.</summary>
public class RubricCriterionDetailDto
{
    public string CriterionCode { get; set; } = string.Empty;
    public string CriterionName { get; set; } = string.Empty;
    public decimal MaxPoints { get; set; }
    public decimal AwardedPoints { get; set; }
    public bool IsMet { get; set; }
    public string Explanation { get; set; } = string.Empty;
}

public class AttemptSnapshotDto
{
    public string AttemptId { get; set; } = string.Empty;
    public string StudentId { get; set; } = string.Empty;
    public string StudentName { get; set; } = string.Empty;
    public string ClassId { get; set; } = string.Empty;
    public string ExamId { get; set; } = string.Empty;
    public int AttemptOrdinal { get; set; } = 1;
    public ExamState ExamState { get; set; } = ExamState.Completed;
    public ReportGradingState GradingState { get; set; } = ReportGradingState.Graded;
    public ExamEvaluationMode EvaluationMode { get; set; } = ExamEvaluationMode.Score;
    public DateTime? CompletedAt { get; set; }
    public string SettingsVersion { get; set; } = "1.0";
    public List<QuestionResultDto> QuestionResults { get; set; } = new();

    public decimal TotalEarnedScore => QuestionResults.Sum(q => q.EarnedScore);
    public decimal TotalMaxScore => QuestionResults.Sum(q => q.MaxScore);

    /// <summary>
    /// Đưa điểm về thang chuẩn 10 (DEC-12)
    /// </summary>
    public decimal NormalizedScore => TotalMaxScore > 0 ? Math.Round((TotalEarnedScore / TotalMaxScore) * 10.0m, 2) : 0m;

    public bool IsPassed { get; set; }

    // Trạng thái phê duyệt điểm (Nhóm 4 - HITL)
    public bool IsFullyFinalized => QuestionResults.Count > 0 && QuestionResults.All(q => q.IsFinalized);
    public bool IsAiGraded => !IsFullyFinalized && QuestionResults.Any(q => q.IsAiGraded || q.AiSuggestedScore > 0);
}

public class StudentReportDto
{
    public string StudentId { get; set; } = string.Empty;
    public string StudentName { get; set; } = string.Empty;
    public string ExamId { get; set; } = string.Empty;
    public string ExamTitle { get; set; } = string.Empty;
    public ExamEvaluationMode EvaluationMode { get; set; } = ExamEvaluationMode.Score;
    public int CompletedAttemptsCount { get; set; }
    public int MaxAllowedAttemptsCount { get; set; }
    public int GradedAttemptsCount { get; set; }
    public int PendingAttemptsCount { get; set; }
    public decimal? AverageScore { get; set; }
    public decimal PassThresholdScore { get; set; } = 6.0m;
    public decimal? PassRatePercentage { get; set; }
    public List<AttemptSnapshotDto> Attempts { get; set; } = new();
}

public class QuestionStatDto
{
    public string QuestionId { get; set; } = string.Empty;
    public string QuestionTitle { get; set; } = string.Empty;
    public string CognitiveLevel { get; set; } = "Vận dụng";
    public int TotalAssessed { get; set; }
    public int PassedCount { get; set; }
    public int FailedCount { get; set; }
    public decimal PassRate { get; set; }
    public decimal FailRate { get; set; }
    public bool IsHardest { get; set; }
    public int Rank { get; set; }
}

public class ClassScoreBinDto
{
    public decimal Score { get; set; }
    public int StudentCount { get; set; }
}

public class ClassGradeExportRowDto
{
    public string StudentId { get; set; } = string.Empty;
    public string StudentName { get; set; } = string.Empty;
    public string ClassId { get; set; } = string.Empty;
    public string Attempt1Score { get; set; } = "Chưa thi";
    public string Attempt2Score { get; set; } = "Chưa thi";
    public string AverageScore { get; set; } = "Chưa có";
    public string ResultStatus { get; set; } = "Chưa đạt";
    public string Note { get; set; } = string.Empty;
}

public class ClassStatisticsDto
{
    public string ClassId { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public string ExamId { get; set; } = string.Empty;
    public string ExamTitle { get; set; } = string.Empty;
    public int TotalStudentsCount { get; set; }
    public decimal AverageClassScore { get; set; }
    public int? FilteredAttemptOrdinal { get; set; }
    public string DataScopeLabel { get; set; } = "Lượt thi số 1";
    public List<QuestionStatDto> QuestionStats { get; set; } = new();
    public List<QuestionStatDto> HardestQuestions { get; set; } = new();
    public List<ClassScoreBinDto> ScoreDistribution { get; set; } = new();
    public List<ClassGradeExportRowDto> GradeExportRows { get; set; } = new();
}
