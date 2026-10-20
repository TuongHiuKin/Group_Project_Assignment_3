using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using AIVES.BLL.Models.VivaInterview;
using AIVES.BLL.Services.VivaInterview;
using AIVES.DAL.Entities;
using AIVES.DAL.Repositories.Interfaces;
using Xunit;

namespace AIVES.Tests.VivaInterview;

public class VivaExamRoomServiceTests
{
    private sealed class InMemoryQuestionRepo : IVivaQuestionRepository
    {
        public readonly List<VivaQuestion> Items = new();

        public Task<VivaQuestion?> GetByIdAsync(string questionId, CancellationToken ct = default)
            => Task.FromResult(Items.FirstOrDefault(x => x.QuestionId == questionId));

        public Task<List<VivaQuestion>> GetQuestionsByExamAsync(string examId, CancellationToken ct = default)
            => Task.FromResult(Items.Where(x => string.Equals(x.ExamId, examId, StringComparison.OrdinalIgnoreCase)).ToList());

        public Task<bool> ExamExistsAsync(string examId, CancellationToken ct = default)
            => Task.FromResult(Items.Any(x => string.Equals(x.ExamId, examId, StringComparison.OrdinalIgnoreCase)));

        public Task AddQuestionsAsync(IEnumerable<VivaQuestion> questions, CancellationToken ct = default)
        {
            Items.AddRange(questions);
            return Task.CompletedTask;
        }

        public Task<List<string>> GetAllExamIdsAsync(CancellationToken ct = default)
            => Task.FromResult(Items.Select(x => x.ExamId).Distinct(StringComparer.OrdinalIgnoreCase).ToList());

        public Task<List<VivaQuestion>> GetAllQuestionsAsync(CancellationToken ct = default)
            => Task.FromResult(Items.ToList());
    }

    [Fact]
    public async Task CreateExamRoomAsync_ValidRequest_SucceedsAndPersists()
    {
        var repo = new InMemoryQuestionRepo();
        var service = new VivaExamRoomService(repo, NullLogger<VivaExamRoomService>.Instance);

        var dto = new CreateExamRoomDto(
            ExamId: "PRN222-SE01",
            Title: "PRN222 Viva Exam",
            DefaultTimeLimitSeconds: 90,
            Questions: new List<CreateExamQuestionDto>
            {
                new("DI Lifetime", "Explain Scoped vs Singleton", 90),
                new("3-Layer Architecture", "Explain BLL vs DAL boundaries", 90)
            }
        );

        var code = await service.CreateExamRoomAsync(dto);

        Assert.Equal("PRN222-SE01", code);
        Assert.Equal(2, repo.Items.Count);
        Assert.All(repo.Items, q => Assert.Equal("PRN222-SE01", q.ExamId));
        Assert.Equal("PRN222-SE01-Q01", repo.Items[0].QuestionId);
        Assert.Equal("PRN222-SE01-Q02", repo.Items[1].QuestionId);
    }

    [Fact]
    public async Task CreateExamRoomAsync_DuplicateExamId_ThrowsInvalidOperationException()
    {
        var repo = new InMemoryQuestionRepo();
        repo.Items.Add(new VivaQuestion { QuestionId = "EX01-Q01", ExamId = "EX01", Title = "T", Content = "C" });
        var service = new VivaExamRoomService(repo, NullLogger<VivaExamRoomService>.Instance);

        var dto = new CreateExamRoomDto(
            ExamId: "EX01",
            Title: "Duplicate Room",
            DefaultTimeLimitSeconds: 90,
            Questions: new List<CreateExamQuestionDto> { new("T2", "C2", 90) }
        );

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateExamRoomAsync(dto));
    }

    [Fact]
    public async Task CreateExamRoomAsync_NoQuestions_ThrowsArgumentException()
    {
        var repo = new InMemoryQuestionRepo();
        var service = new VivaExamRoomService(repo, NullLogger<VivaExamRoomService>.Instance);

        var dto = new CreateExamRoomDto(
            ExamId: "TEST-01",
            Title: "Empty Room",
            DefaultTimeLimitSeconds: 90,
            Questions: new List<CreateExamQuestionDto>()
        );

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateExamRoomAsync(dto));
    }

    [Fact]
    public async Task ValidateExamRoomCodeAsync_ReturnsExpectedResult()
    {
        var repo = new InMemoryQuestionRepo();
        repo.Items.Add(new VivaQuestion { QuestionId = "NET8-Q01", ExamId = "NET8", Title = "T", Content = "C" });
        var service = new VivaExamRoomService(repo, NullLogger<VivaExamRoomService>.Instance);

        Assert.True(await service.ValidateExamRoomCodeAsync("NET8"));
        Assert.True(await service.ValidateExamRoomCodeAsync("net8"));
        Assert.False(await service.ValidateExamRoomCodeAsync("NONEXISTENT"));
        Assert.False(await service.ValidateExamRoomCodeAsync(""));
    }

    [Fact]
    public async Task GetAvailableExamRoomsAsync_ReturnsGroupedSummaries()
    {
        var repo = new InMemoryQuestionRepo();
        repo.Items.Add(new VivaQuestion { QuestionId = "E1-Q01", ExamId = "E1", Title = "Title 1", Content = "C1", TimeLimitSeconds = 90 });
        repo.Items.Add(new VivaQuestion { QuestionId = "E1-Q02", ExamId = "E1", Title = "Title 2", Content = "C2", TimeLimitSeconds = 90 });
        repo.Items.Add(new VivaQuestion { QuestionId = "E2-Q01", ExamId = "E2", Title = "Title 3", Content = "C3", TimeLimitSeconds = 60 });

        var service = new VivaExamRoomService(repo, NullLogger<VivaExamRoomService>.Instance);
        var rooms = await service.GetAvailableExamRoomsAsync();

        Assert.Equal(2, rooms.Count);
        var e1 = rooms.First(r => r.ExamId == "E1");
        Assert.Equal(2, e1.QuestionCount);
        Assert.Equal(2, e1.QuestionTitles.Count);
    }
}
