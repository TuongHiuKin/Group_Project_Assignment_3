using AIVES.BLL.Repositories.ExamReporting;
using AIVES.BLL.Services.ExamReporting;
using AIVES.BLL.Services.MockData;
using Xunit;

namespace AIVES.Tests.ExamReporting;

public sealed class MockExamDataCatalogTests
{
    // Expected rubric scores (Q01, Q02, Q03) per mock attempt; keeps the class statistics spread across all score bands.
    public static TheoryData<string, string, decimal, decimal, decimal> ExpectedScores => new()
    {
        { "minhanh@mock.aives.local", "a1", 10m, 10m, 7m },
        { "quocbao@mock.aives.local", "a1", 7m, 6.5m, 10m },
        { "thuha@mock.aives.local", "a1", 3.5m, 7m, 3.5m },
        { "thuha@mock.aives.local", "a2", 6.5m, 10m, 7m },
        { "giahuy@mock.aives.local", "a1", 3m, 3.5m, 0m },
        { "ngoclan@mock.aives.local", "a1", 3.5m, 6.5m, 6.5m },
        { "tuankiet@mock.aives.local", "a1", 3.5m, 3.5m, 6.5m },
        { "tuankiet@mock.aives.local", "a2", 10m, 7m, 7m }
    };

    [Theory]
    [MemberData(nameof(ExpectedScores))]
    public void MockAttempt_IsGradedByTeacherRubric_ToTheExpectedScores(string email, string attemptKey, decimal q01, decimal q02, decimal q03)
    {
        var grader = new RubricGradingService();
        var attempt = MockExamDataCatalog.Students.Single(s => s.Account.Email == email).Attempts.Single(a => a.Key == attemptKey);

        var scores = attempt.Answers.Select(a =>
        {
            var rubric = grader.GetRubric(a.QuestionId)!;
            var result = grader.Grade(a.QuestionId, $"{rubric.Title} {a.FollowUpQuestion}", $"{a.InitialAnswer} {a.FollowUpAnswer}",
                ExamReportRepository.DefaultPassThresholdScore)!;
            Assert.True(result.HasRelevantAnswer, $"{email}/{attemptKey}/{a.QuestionId} must count as a real answer");
            return result.EarnedScore;
        }).ToArray();

        Assert.Equal(new[] { q01, q02, q03 }, scores);
    }

    [Fact]
    public void Catalog_CoversEveryAttemptAndQuestionOnce()
    {
        var attempts = MockExamDataCatalog.Students.SelectMany(s => s.Attempts.Select(a => (s.Account, a))).ToList();

        Assert.Equal(ExpectedScores.Count, attempts.Count);
        Assert.All(attempts, x => Assert.Equal(new[] { "EXAM01-Q01", "EXAM01-Q02", "EXAM01-Q03" }, x.a.Answers.Select(q => q.QuestionId)));
    }

    [Fact]
    public void Catalog_EmailsAndSessionIdsAreUniqueAndStable()
    {
        var emails = MockExamDataCatalog.TestAccounts.Concat(MockExamDataCatalog.Students.Select(s => s.Account)).Select(a => a.Email).ToList();
        Assert.Equal(emails.Count, emails.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(emails, e => Assert.Equal(e.ToLowerInvariant(), e));

        var ids = MockExamDataCatalog.Students.SelectMany(s => s.Attempts.Select(a => MockExamDataSeeder.SessionIdFor(s.Account, a))).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());

        var first = MockExamDataCatalog.Students[0];
        Assert.Equal(MockExamDataSeeder.SessionIdFor(first.Account, first.Attempts[0]), MockExamDataSeeder.SessionIdFor(first.Account, first.Attempts[0]));
    }
}
