using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using AIVES.BLL;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Models.Ai;
using AIVES.BLL.Models.Grading;
using AIVES.BLL.Services.Ai.Mocks;
using AIVES.BLL.Services.ExamReporting;

namespace AIVES.Tests.Ai;

public class MockAnswerGradingServiceTests
{
    private readonly MockAnswerGradingService _service = new(new RubricGradingService());

    [Fact]
    public async Task GradeAnswerAsync_Throws_ArgumentNullException_When_Input_Is_Null()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _service.GradeAnswerAsync(null!));
    }

    [Fact]
    public async Task GradeAnswerAsync_WithExplicitRubricCriteria_MeetsKeywords_AwardsCorrectPoints()
    {
        var input = new GradingInputDto
        {
            SessionId = Guid.NewGuid(),
            QuestionId = "Q1",
            QuestionText = "Giải thích vòng đời của DbContext trong ASP.NET Core?",
            BloomLevel = "Understand",
            MaxPoints = 10m,
            StudentAnswerTranscript = "DbContext thường được đăng ký dưới dạng Scoped lifetime trong ASP.NET Core, mỗi HTTP request sẽ có một instance riêng và tự động giải phóng khi request kết thúc.",
            FollowUpTurns = new List<string> { "Ngoài ra, không nên dùng DbContext như Singleton vì không thread-safe." },
            RubricCriteria = new List<RubricCriterionDto>
            {
                new()
                {
                    CriterionCode = "C1",
                    CriterionName = "Scoped Lifetime",
                    MaxPoints = 5m,
                    Keywords = new List<string> { "scoped", "http request" }
                },
                new()
                {
                    CriterionCode = "C2",
                    CriterionName = "Thread Safety & Singleton Warning",
                    MaxPoints = 5m,
                    Keywords = new List<string> { "singleton", "thread" }
                }
            }
        };

        var result = await _service.GradeAnswerAsync(input);

        Assert.NotNull(result);
        Assert.Equal(input.SessionId, result.SessionId);
        Assert.Equal(input.QuestionId, result.QuestionId);
        Assert.Equal(10m, result.AiScore);
        Assert.Equal(GradingState.AiGraded, result.State);
        Assert.Equal("Mock", result.AiProvider);
        Assert.Equal(2, result.CriteriaOutcomes.Count);
        Assert.All(result.CriteriaOutcomes, c => Assert.True(c.IsMet));
        Assert.Contains("Scoped Lifetime", result.Strengths);
        Assert.Contains("Thread Safety", result.Strengths);
    }

    [Fact]
    public async Task GradeAnswerAsync_WithPartialKeywords_CalculatesPartialScoreAndIdentifiesWeaknesses()
    {
        var input = new GradingInputDto
        {
            SessionId = Guid.NewGuid(),
            QuestionId = "Q2",
            QuestionText = "So sánh Interface và Abstract Class trong C#?",
            MaxPoints = 10m,
            StudentAnswerTranscript = "Interface cho phép một class kế thừa nhiều interface, và các phương thức chỉ chứa chữ ký.",
            RubricCriteria = new List<RubricCriterionDto>
            {
                new()
                {
                    CriterionCode = "C1",
                    CriterionName = "Đa kế thừa",
                    MaxPoints = 5m,
                    Keywords = new List<string> { "ke thua nhieu", "interface" }
                },
                new()
                {
                    CriterionCode = "C2",
                    CriterionName = "Abstract class có constructor và trạng thái",
                    MaxPoints = 5m,
                    Keywords = new List<string> { "constructor", "field", "bien trang thai" }
                }
            }
        };

        var result = await _service.GradeAnswerAsync(input);

        Assert.NotNull(result);
        Assert.Equal(5m, result.AiScore);
        var c1 = result.CriteriaOutcomes.First(c => c.CriterionCode == "C1");
        var c2 = result.CriteriaOutcomes.First(c => c.CriterionCode == "C2");
        Assert.True(c1.IsMet);
        Assert.Equal(5m, c1.AwardedPoints);
        Assert.False(c2.IsMet);
        Assert.Equal(0m, c2.AwardedPoints);
        Assert.Contains("Abstract class có constructor", result.Weaknesses);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task GradeAnswerAsync_WithEmptyAnswer_AwardsZeroAndReportsNoRelevantAnswer(string? emptyAnswer)
    {
        var input = new GradingInputDto
        {
            SessionId = Guid.NewGuid(),
            QuestionId = "Q3",
            QuestionText = "Giải thích Dependency Injection?",
            MaxPoints = 10m,
            StudentAnswerTranscript = emptyAnswer!,
            RubricCriteria = new List<RubricCriterionDto>
            {
                new() { CriterionCode = "C1", CriterionName = "Khái niệm DI", MaxPoints = 10m, Keywords = new List<string> { "inversion of control", "loose coupling" } }
            }
        };

        var result = await _service.GradeAnswerAsync(input);

        Assert.NotNull(result);
        Assert.Equal(0m, result.AiScore);
        Assert.False(result.CriteriaOutcomes[0].IsMet);
        Assert.Equal(RubricGradingService.NoRelevantAnswerFeedback, result.AiFeedback);
    }

    [Fact]
    public async Task GradeAnswerAsync_ClampsScoreWithinMaxPoints()
    {
        var input = new GradingInputDto
        {
            SessionId = Guid.NewGuid(),
            QuestionId = "Q4",
            QuestionText = "Câu hỏi điểm tối đa là 5",
            MaxPoints = 5m, // max là 5
            StudentAnswerTranscript = "Đạt cả tiêu chí 1 và tiêu chí 2 với từ khóa khớp hoàn toàn.",
            RubricCriteria = new List<RubricCriterionDto>
            {
                new() { CriterionCode = "C1", CriterionName = "Tiêu chí 1", MaxPoints = 4m, Keywords = new List<string> { "tieu chi 1" } },
                new() { CriterionCode = "C2", CriterionName = "Tiêu chí 2", MaxPoints = 4m, Keywords = new List<string> { "tieu chi 2" } }
            }
        };

        var result = await _service.GradeAnswerAsync(input);

        Assert.NotNull(result);
        Assert.True(result.AiScore <= 5m);
        Assert.Equal(5m, result.AiScore); // clamped to MaxPoints 5m
    }

    [Fact]
    public async Task GradeAnswerAsync_FallsBackToRubricCatalog_WhenRubricCriteriaEmpty()
    {
        // EXAM01-Q01 is a known seeded question in RubricCatalog
        var input = new GradingInputDto
        {
            SessionId = Guid.NewGuid(),
            QuestionId = "EXAM01-Q01",
            QuestionText = "Trình bày vòng đời của DbContext trong ASP.NET Core?",
            MaxPoints = 10m,
            StudentAnswerTranscript = "DbContext tạo ra cho từng request và giúp ngăn ngừa rò rỉ bộ nhớ từ change tracker.",
            RubricCriteria = new List<RubricCriterionDto>() // Rỗng -> Fallback sang RubricCatalog
        };

        var result = await _service.GradeAnswerAsync(input);

        Assert.NotNull(result);
        Assert.True(result.AiScore > 0m);
        Assert.NotEmpty(result.CriteriaOutcomes);
        Assert.Equal(GradingState.AiGraded, result.State);
    }

    [Fact]
    public void DependencyInjection_AddMockAiServices_Resolves_MockAnswerGradingService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMockAiServices();

        using var sp = services.BuildServiceProvider();
        var gradingService = sp.GetService<IAnswerGradingService>();

        Assert.NotNull(gradingService);
        Assert.IsType<MockAnswerGradingService>(gradingService);
    }

    [Fact]
    public void DependencyInjection_AddAiServices_Resolves_IAnswerGradingService_InDefaultMockMode()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddAiServices();

        using var sp = services.BuildServiceProvider();
        var gradingService = sp.GetService<IAnswerGradingService>();

        Assert.NotNull(gradingService);
        Assert.IsType<MockAnswerGradingService>(gradingService);
    }
}
