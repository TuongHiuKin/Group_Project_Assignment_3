namespace AIVES.BLL.Models.VivaInterview;

public sealed record CreateExamQuestionDto(
    string Title,
    string Content,
    int TimeLimitSeconds = 90
);

public sealed record CreateExamRoomDto(
    string ExamId,
    string Title,
    int DefaultTimeLimitSeconds,
    List<CreateExamQuestionDto> Questions
);

public sealed record ExamRoomSummaryDto(
    string ExamId,
    string Title,
    int QuestionCount,
    int DefaultTimeLimitSeconds,
    List<string> QuestionTitles
);

public sealed record ExamRoomDetailDto(
    string ExamId,
    string Title,
    int QuestionCount,
    int DefaultTimeLimitSeconds,
    List<CreateExamQuestionDto> Questions
);
