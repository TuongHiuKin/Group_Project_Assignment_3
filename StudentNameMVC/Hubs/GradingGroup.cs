using System.Text.RegularExpressions;

namespace StudentNameMVC.Hubs;

/// <summary>Lecturer grading group per exam: <c>grading-{examId}</c> (docs/G4-CONTRACTS.md §5.1).</summary>
public static partial class GradingGroup
{
    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex ExamIdPattern();

    public static bool IsValidExamId(string? examId) => examId is not null && ExamIdPattern().IsMatch(examId.Trim());

    /// <summary>Exam IDs are normalized to upper case so "exam01" and "EXAM01" share one group.</summary>
    public static string Name(string examId)
    {
        if (!IsValidExamId(examId)) throw new ArgumentException("A valid exam ID is required.", nameof(examId));
        return $"grading-{examId.Trim().ToUpperInvariant()}";
    }
}
