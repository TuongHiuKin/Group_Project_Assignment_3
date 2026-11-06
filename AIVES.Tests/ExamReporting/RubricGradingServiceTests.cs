using AIVES.BLL.Models.ExamReporting;
using AIVES.BLL.Services.ExamReporting;
using Xunit;

namespace AIVES.Tests.ExamReporting;

public class RubricGradingServiceTests
{
    private const string Q01 = "EXAM01-Q01";
    private const string Q01Text = "DbContext Lifetime Hãy giải thích vì sao DbContext trong ASP.NET Core phải được đăng ký với Scoped lifetime thay vì Singleton, và điều gì sẽ xảy ra với Entity Tracker và kết nối cơ sở dữ liệu nếu một service Singleton giữ tham chiếu đến DbContext?";
    private const decimal Threshold = 6.0m;

    private readonly RubricGradingService _service = new();

    [Fact]
    public void Catalog_Exam01_HasThreeQuestionsWithWeightedKeywordCriteria()
    {
        var rubrics = _service.GetRubrics("EXAM01");

        Assert.Equal(new[] { "EXAM01-Q01", "EXAM01-Q02", "EXAM01-Q03" }, rubrics.Select(r => r.QuestionId));
        Assert.All(rubrics, r =>
        {
            Assert.Equal(3, r.Criteria.Count);
            Assert.Equal(10m, r.Criteria.Sum(c => r.PointsFor(c)));
            Assert.All(r.Criteria, c => Assert.NotEmpty(c.Keywords));
        });
        Assert.Same(RubricCatalog.All[0], _service.GetRubric("exam01-q01"));
    }

    [Fact]
    public void Grade_FullAnswer_MeetsAllCriteriaAndPasses()
    {
        var result = _service.Grade(Q01, Q01Text,
            "DbContext là Scoped vì mỗi HTTP request có một instance riêng. Nếu giữ trong Singleton thì ChangeTracker tích lũy entity gây rò rỉ bộ nhớ, và DbContext không thread-safe nên nhiều luồng dùng đồng thời sẽ lỗi.",
            Threshold);

        Assert.NotNull(result);
        Assert.True(result.HasRelevantAnswer);
        Assert.Equal(10m, result.EarnedScore);
        Assert.True(result.IsPassed);
        Assert.All(result.Outcomes, o => Assert.True(o.IsMet));
        Assert.Contains("Vòng đời Scoped", result.Strengths);
        Assert.Empty(result.Weaknesses);
        Assert.Contains("đạt 3/3 tiêu chí", result.Feedback);
    }

    [Theory]
    [InlineData("Vì mỗi request cần một instance riêng, tạo mới và hủy theo vòng đời của request đó.", 3.5, false)]
    [InlineData("Singleton làm ChangeTracker tích lũy entity gây memory leak, và lỗi khi nhiều luồng truy cập đồng thời.", 6.5, true)]
    [InlineData("Mỗi HTTP request có DbContext riêng, nếu dùng chung thì ChangeTracker phình to gây rò rỉ bộ nhớ.", 7.0, true)]
    [InlineData("Em nghĩ cái này liên quan tới cấu hình dịch vụ trong ứng dụng web.", 0, false)]
    public void Grade_ScoreDependsOnAnswerContent(string transcript, double expectedScore, bool expectedPassed)
    {
        var result = _service.Grade(Q01, Q01Text, transcript, Threshold);

        Assert.NotNull(result);
        Assert.Equal((decimal)expectedScore, result.EarnedScore);
        Assert.Equal(expectedPassed, result.IsPassed);
    }

    [Fact]
    public void Grade_PartialAnswer_ListsMissingCriteriaAsImprovements()
    {
        var result = _service.Grade(Q01, Q01Text, "Vì mỗi request cần một instance riêng, tạo mới và hủy theo vòng đời của request đó.", Threshold);

        Assert.NotNull(result);
        var met = Assert.Single(result.Outcomes, o => o.IsMet);
        Assert.Equal("Q01-C1", met.CriterionId);
        Assert.Contains("mỗi request", met.MatchedKeywords);
        Assert.Contains("Cần bổ sung", result.Weaknesses);
        Assert.Contains("đa luồng", result.Weaknesses);
        Assert.Contains("đạt 1/3 tiêu chí", result.Feedback);
    }

    [Theory]
    [InlineData("Hãy giải thích vì sao DbContext trong ASP.NET Core phải được đăng ký với scoped lifetime thay vì singleton và điều gì sẽ xảy ra với entity tracker và kết nối cơ sở dữ liệu nếu một service singleton giữ tham chiếu đến DbContext?")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Grade_EmptyOrQuestionEcho_GetsZeroWithNoRelevantAnswerFeedback(string? transcript)
    {
        var result = _service.Grade(Q01, Q01Text, transcript, Threshold);

        Assert.NotNull(result);
        Assert.False(result.HasRelevantAnswer);
        Assert.Equal(0m, result.EarnedScore);
        Assert.False(result.IsPassed);
        Assert.All(result.Outcomes, o => Assert.False(o.IsMet));
        Assert.Equal(RubricGradingService.NoRelevantAnswerFeedback, result.Feedback);
        Assert.Empty(result.Strengths);
    }

    [Fact]
    public void Grade_UsesGivenPassThreshold()
    {
        const string answer = "Singleton làm ChangeTracker tích lũy entity gây memory leak, và lỗi khi nhiều luồng truy cập đồng thời.";

        Assert.True(_service.Grade(Q01, Q01Text, answer, 6.0m)!.IsPassed);
        Assert.False(_service.Grade(Q01, Q01Text, answer, 7.0m)!.IsPassed);
    }

    [Fact]
    public void Grade_QuestionWithoutRubric_ReturnsNull()
    {
        Assert.Null(_service.Grade("EXAM99-Q01", "Câu hỏi", "Một câu trả lời dài đủ nhiều từ mới.", Threshold));
    }

    [Theory]
    [InlineData("Rò rỉ BỘ NHỚ!", "ro ri bo nho")]
    [InlineData("thread-safe, đồng thời.", "thread safe dong thoi")]
    [InlineData("  Đa   luồng\n", "da luong")]
    public void Normalize_LowercasesStripsDiacriticsAndPunctuation(string input, string expected)
    {
        Assert.Equal(expected, RubricGradingService.Normalize(input));
    }

    [Fact]
    public void Grade_KeywordMustMatchWholeWords()
    {
        var rubric = new QuestionRubric
        {
            QuestionId = "T-Q1",
            ExamId = "T",
            Criteria = new[] { new RubricCriterion { Id = "C1", Name = "Claim", Weight = 1m, Keywords = new[] { "claim" } } }
        };
        var service = new RubricGradingService(new[] { rubric });

        Assert.Equal(0m, service.Grade("T-Q1", "", "hệ thống dùng claimsprincipal để lấy thông tin", Threshold)!.EarnedScore);
        Assert.Equal(10m, service.Grade("T-Q1", "", "hệ thống đọc claim của người dùng đăng nhập", Threshold)!.EarnedScore);
    }

    [Fact]
    public void Default_Constructor_Does_Not_Throw_NullReference_On_GetRubrics_Or_GetRubric()
    {
        var service = new RubricGradingService();

        var rubrics = service.GetRubrics("EXAM01");
        var rubric = service.GetRubric("EXAM01-Q01");

        Assert.NotNull(rubrics);
        Assert.NotEmpty(rubrics);
        Assert.NotNull(rubric);
        Assert.Equal("EXAM01-Q01", rubric.QuestionId);
    }

    [Fact]
    public void RubricGradingService_Supports_Initialization_With_IVivaRubricRepository()
    {
        var repoInterface = Type.GetType("AIVES.DAL.Repositories.Interfaces.IVivaRubricRepository, AIVES.DAL");
        Assert.True(repoInterface != null, "IVivaRubricRepository interface must exist in AIVES.DAL.Repositories.Interfaces.");

        var ctor = typeof(RubricGradingService).GetConstructor(new[] { repoInterface });
        Assert.True(ctor != null, "RubricGradingService must provide a constructor accepting IVivaRubricRepository.");
    }

    [Fact]
    public void RubricGradingService_Supports_Initialization_With_Rubric_Or_Criteria_List()
    {
        var sampleRubrics = new List<QuestionRubric>
        {
            new()
            {
                QuestionId = "TEST-Q01",
                ExamId = "TEST",
                Title = "Test Question",
                Criteria = new List<RubricCriterion>
                {
                    new() { Id = "C1", Name = "Criterion 1", Weight = 1.0m, Keywords = new[] { "keyword" } }
                }
            }
        };

        var service = new RubricGradingService(sampleRubrics);
        var rubrics = service.GetRubrics("TEST");
        var rubric = service.GetRubric("TEST-Q01");

        Assert.NotNull(rubrics);
        Assert.Single(rubrics);
        Assert.NotNull(rubric);
        Assert.Equal("TEST-Q01", rubric.QuestionId);
    }
}
