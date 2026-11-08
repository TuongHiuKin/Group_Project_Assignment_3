using AIVES.BLL.Models.ExamReporting;
using AIVES.BLL.Services.ExamReporting;
using AIVES.DAL.Context;
using AIVES.DAL.Entities;
using AIVES.DAL.Repositories.Implementations;
using AIVES.DAL.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AIVES.Tests.ExamReporting;

/// <summary>
/// G4-DB-07: rubric criteria must come from SQL Server (VivaRubricCriteria), not the hard-coded
/// <see cref="RubricCatalog"/>. RubricCatalog stays as the fallback for databases that have not run 006.
/// </summary>
public class DbBackedRubricTests
{
    private sealed class FakeRubricRepository : IVivaRubricRepository
    {
        public List<VivaRubricCriterion> Criteria { get; } = new();

        public bool ThrowOnRead { get; set; }

        public Task<List<VivaRubricCriterion>> GetCriteriaByQuestionAsync(string questionId, CancellationToken ct = default)
        {
            if (ThrowOnRead) throw new InvalidOperationException("Invalid object name 'VivaRubricCriteria'.");
            return Task.FromResult(Criteria.Where(c => c.QuestionId == questionId)
                .OrderBy(c => c.CriterionCode).ToList());
        }

        public Task<List<VivaRubricCriterion>> GetCriteriaByExamAsync(string examId, CancellationToken ct = default)
        {
            if (ThrowOnRead) throw new InvalidOperationException("Invalid object name 'VivaRubricCriteria'.");
            return Task.FromResult(Criteria.OrderBy(c => c.QuestionId).ThenBy(c => c.CriterionCode).ToList());
        }
    }

    private static VivaRubricCriterion Criterion(
        string id, string questionId, string code, string name, decimal weight, decimal maxPoints, string keywordsJson) =>
        new()
        {
            Id = id,
            QuestionId = questionId,
            CriterionCode = code,
            CriterionName = name,
            Description = name,
            Weight = weight,
            MaxPoints = maxPoints,
            KeywordsJson = keywordsJson
        };

    private static FakeRubricRepository SeededRepository() => new()
    {
        Criteria =
        {
            Criterion("RC-DB-01", "EXAM01-Q01", "Q01-C1", "Vòng đời request", 5m, 5m, "[\"mỗi request\"]"),
            Criterion("RC-DB-02", "EXAM01-Q01", "Q01-C2", "Rò rỉ bộ nhớ", 5m, 5m, "[\"rò rỉ bộ nhớ\"]")
        }
    };

    [Fact]
    public async Task GetRubricAsync_reads_criteria_from_the_database()
    {
        var service = new RubricGradingService(SeededRepository());

        var rubric = await service.GetRubricAsync("EXAM01-Q01");

        Assert.NotNull(rubric);
        Assert.Equal(2, rubric!.Criteria.Count);
        Assert.Equal(new[] { "Q01-C1", "Q01-C2" }, rubric.Criteria.Select(c => c.Id));
        Assert.Equal(10m, rubric.MaxScore);
        // Proof the DB row (not RubricCatalog's 3 Vietnamese criteria) was used.
        Assert.DoesNotContain(rubric.Criteria, c => c.Name.Contains("ChangeTracker", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetRubricsAsync_groups_criteria_by_question_and_parses_keywords()
    {
        var service = new RubricGradingService(SeededRepository());

        var rubrics = await service.GetRubricsAsync("EXAM01");

        var single = Assert.Single(rubrics);
        Assert.Equal("EXAM01-Q01", single.QuestionId);
        Assert.Equal(new[] { "mỗi request" }, single.Criteria[0].Keywords);
    }

    [Fact]
    public async Task GetRubricAsync_falls_back_to_catalog_when_the_table_is_missing()
    {
        var repo = SeededRepository();
        repo.ThrowOnRead = true;
        var service = new RubricGradingService(repo);

        var rubric = await service.GetRubricAsync("EXAM01-Q01");

        Assert.NotNull(rubric);
        Assert.Equal(3, rubric!.Criteria.Count);
        Assert.Contains(rubric.Criteria, c => c.Name.StartsWith("Vòng đời Scoped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetRubricAsync_falls_back_to_catalog_when_the_table_is_empty()
    {
        var service = new RubricGradingService(new FakeRubricRepository());

        var rubric = await service.GetRubricAsync("EXAM01-Q02");

        Assert.NotNull(rubric);
        Assert.NotEmpty(rubric!.Criteria);
    }

    [Fact]
    public async Task Grade_with_a_database_rubric_scores_from_those_keywords_only()
    {
        var service = new RubricGradingService(SeededRepository());
        var rubric = await service.GetRubricAsync("EXAM01-Q01");

        var matched = service.Grade(rubric!, "DbContext Scoped theo mỗi request", "Vì mỗi request cần instance riêng.", 5m);
        var missed = service.Grade(rubric!, "DbContext Scoped theo mỗi request", "Tôi không nhớ rõ khái niệm này.", 5m);

        Assert.NotNull(matched);
        Assert.Equal(5m, matched!.EarnedScore);
        Assert.NotNull(missed);
        Assert.Equal(0m, missed!.EarnedScore);
    }

    [Fact]
    public async Task Repository_returns_criteria_from_the_EF_model()
    {
        var options = new DbContextOptionsBuilder<AIVESDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var context = new AIVESDbContext(options);
        context.VivaQuestions.Add(new VivaQuestion
        {
            QuestionId = "EXAM01-Q01",
            ExamId = "EXAM01",
            Title = "DbContext Lifetime",
            Content = "Giải thích DbContext lifetime."
        });
        context.VivaRubricCriteria.AddRange(SeededRepository().Criteria);
        await context.SaveChangesAsync();

        var repo = new VivaRubricRepository(context);
        var service = new RubricGradingService(repo);

        var byQuestion = await repo.GetCriteriaByQuestionAsync("EXAM01-Q01");
        var byExam = await repo.GetCriteriaByExamAsync("EXAM01");
        var rubric = await service.GetRubricAsync("EXAM01-Q01");

        Assert.Equal(2, byQuestion.Count);
        Assert.Equal(2, byExam.Count);
        Assert.NotNull(rubric);
        Assert.Equal(2, rubric!.Criteria.Count);
    }
}
