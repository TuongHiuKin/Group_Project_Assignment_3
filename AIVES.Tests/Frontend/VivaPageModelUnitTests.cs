using System.Security.Claims;
using System.Text;
using AIVES.BLL.Interfaces.VivaInterview;
using AIVES.BLL.Models.VivaInterview;
using AIVES.BLL.Security;
using AIVES.DAL.Entities;
using AIVES.DAL.Repositories.Interfaces;
using AIVES.Tests.Realtime;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging.Abstractions;
using StudentNameMVC.Pages.Viva;
using Xunit;

namespace AIVES.Tests.Frontend;

public sealed class VivaPageModelUnitTests
{
    private readonly RealtimeData _data = new();
    private readonly RealtimeAi _ai = new();
    private readonly RecordingPublisher _publisher = new();

    private PageContext CreatePageContext(string userId = "SV01", string userName = "Student One", string role = ApplicationRoles.Staff)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Name, userName),
            new(ClaimTypes.Role, role)
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        var httpContext = new DefaultHttpContext
        {
            User = principal
        };

        return new PageContext
        {
            HttpContext = httpContext
        };
    }

    [Fact]
    public void IndexModel_OnPost_WithEmptyInput_ReturnsPageWithErrorMessage()
    {
        var model = new IndexModel(NullLogger<IndexModel>.Instance)
        {
            SessionIdInput = ""
        };

        var result = model.OnPost();

        Assert.IsType<PageResult>(result);
        Assert.False(string.IsNullOrEmpty(model.ErrorMessage));
        Assert.Contains("Vui lòng nhập mã phiên", model.ErrorMessage);
    }

    [Fact]
    public void IndexModel_OnPost_WithInvalidGuid_ReturnsPageWithErrorMessage()
    {
        var model = new IndexModel(NullLogger<IndexModel>.Instance)
        {
            SessionIdInput = "not-a-guid"
        };

        var result = model.OnPost();

        Assert.IsType<PageResult>(result);
        Assert.Contains("không hợp lệ", model.ErrorMessage);
    }

    [Fact]
    public void IndexModel_OnPost_WithValidGuid_RedirectsToExamPage()
    {
        var validGuid = Guid.NewGuid();
        var model = new IndexModel(NullLogger<IndexModel>.Instance)
        {
            SessionIdInput = validGuid.ToString()
        };

        var result = model.OnPost();

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Viva/Exam", redirect.PageName);
        Assert.Equal(validGuid, redirect.RouteValues?["sessionId"]);
    }

    private sealed class StubOrchestrator : IVivaSessionOrchestrator
    {
        public string? CapturedExamId { get; private set; }
        public string? CapturedStudentId { get; private set; }
        public Guid ExpectedSessionId { get; set; } = Guid.NewGuid();
        public Guid ExpectedTurnId { get; set; } = Guid.NewGuid();
        public Guid? CapturedFollowUpSessionId { get; private set; }
        public Guid? CapturedFollowUpTurnId { get; private set; }
        public bool ShouldFailFollowUp { get; set; }

        public Task<VivaSessionStatusDto> InitializeSessionAsync(string examId, string studentId, CancellationToken ct = default)
        {
            CapturedExamId = examId;
            CapturedStudentId = studentId;
            return Task.FromResult(new VivaSessionStatusDto(ExpectedSessionId, examId, studentId, VivaSessionState.Created, 0, 2, DateTime.UtcNow, null));
        }

        public Task<bool> AuthorizeUserForSessionAsync(Guid sessionId, string userId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<VivaQuestionDto> StartSessionAsync(Guid sessionId, CancellationToken ct = default) => throw new NotImplementedException();

        public Task<VivaTranscriptDto> SubmitAnswerAudioAsync(VivaAnswerSubmissionDto submission, CancellationToken ct = default)
        {
            return Task.FromResult(new VivaTranscriptDto(
                submission.SessionId,
                ExpectedTurnId,
                submission.QuestionId,
                "Transcript result",
                0.95m,
                DateTime.UtcNow
            ));
        }

        public Task<VivaFollowUpDto?> GenerateFollowUpAsync(Guid sessionId, Guid turnId, CancellationToken ct = default)
        {
            CapturedFollowUpSessionId = sessionId;
            CapturedFollowUpTurnId = turnId;
            if (ShouldFailFollowUp)
            {
                throw new InvalidOperationException("Follow-up generation simulated failure");
            }

            return Task.FromResult<VivaFollowUpDto?>(new VivaFollowUpDto(
                sessionId,
                turnId,
                "Q_FOLLOW_UP_01",
                "Follow-up content",
                1,
                "/audio/mock-question.mp3"
            ));
        }

        public Task<VivaSessionStatusDto> CompleteSessionAsync(Guid sessionId, CancellationToken ct = default) => throw new NotImplementedException();
    }

    [Fact]
    public async Task IndexModel_OnPostCreateAsync_WhenOrchestratorIsNull_ReturnsPageWithErrorMessage()
    {
        var model = new IndexModel(NullLogger<IndexModel>.Instance)
        {
            PageContext = CreatePageContext(),
            ExamIdInput = "EXAM01"
        };

        var result = await model.OnPostCreateAsync();

        Assert.IsType<PageResult>(result);
        Assert.False(model.IsOrchestratorAvailable);
        Assert.Equal("Chức năng thi vấn đáp chưa được bật.", model.ErrorMessage);
    }

    [Fact]
    public async Task IndexModel_OnPostCreateAsync_WhenUserHasNoNameIdentifier_ReturnsChallengeResult()
    {
        var stub = new StubOrchestrator();
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) };
        var model = new IndexModel(NullLogger<IndexModel>.Instance, stub)
        {
            PageContext = new PageContext { HttpContext = httpContext },
            ExamIdInput = "EXAM01"
        };

        var result = await model.OnPostCreateAsync();

        Assert.IsType<ChallengeResult>(result);
    }

    [Fact]
    public async Task IndexModel_OnPostCreateAsync_WhenSuccessful_RedirectsToExamWithSessionId()
    {
        var stub = new StubOrchestrator();
        var model = new IndexModel(NullLogger<IndexModel>.Instance, stub)
        {
            PageContext = CreatePageContext(userId: "SV_TEST_01"),
            ExamIdInput = "EXAM99"
        };

        var result = await model.OnPostCreateAsync();

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Viva/Exam", redirect.PageName);
        Assert.Equal(stub.ExpectedSessionId, redirect.RouteValues?["sessionId"]);
        Assert.Equal("EXAM99", stub.CapturedExamId);
        Assert.Equal("SV_TEST_01", stub.CapturedStudentId);
    }

    private sealed class UnconfiguredAiStore : AIVES.BLL.Interfaces.Ai.IAiCredentialStore
    {
        public AIVES.BLL.Interfaces.Ai.AiCredentialStatus GetStatus() => new(false, false, null, null);
        public string? GetApiKey() => null;
        public Task SaveAsync(string? newApiKey, bool useRealAi, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
        public AIVES.BLL.Models.Ai.AiConfigurationModel GetConfiguration() => new() { UseRealAi = false, Profiles = [] };
        public Task SaveConfigurationAsync(AIVES.BLL.Models.Ai.AiConfigurationModel updated, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task IndexModel_OnPostCreateAsync_WhenAiIsNotConfigured_BlocksSessionCreationWithVietnameseWarning()
    {
        var stub = new StubOrchestrator();
        var credentialStore = new UnconfiguredAiStore();
        var aiOptions = Microsoft.Extensions.Options.Options.Create(
            new AIVES.BLL.Options.AiServiceOptions { UseMock = false, AllowMockFallback = false });

        var model = new IndexModel(NullLogger<IndexModel>.Instance, stub, credentialStore, aiOptions)
        {
            PageContext = CreatePageContext(userId: "SV_TEST_01"),
            ExamIdInput = "EXAM01"
        };

        Assert.False(model.IsAiConfigured);
        var result = await model.OnPostCreateAsync();

        Assert.IsType<PageResult>(result);
        Assert.Contains("Chưa cấu hình AI", model.ErrorMessage);
        Assert.Null(stub.CapturedExamId);
    }

    [Fact]
    public async Task ExamModel_OnGetAsync_WithNullOrEmptySessionId_RedirectsToIndex()
    {
        var model = new ExamModel(NullLogger<ExamModel>.Instance)
        {
            PageContext = CreatePageContext()
        };

        var result = await model.OnGetAsync(null);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Viva/Index", redirect.PageName);
    }

    [Fact]
    public async Task ExamModel_OnGetAsync_WhenUserIsUnauthorized_RedirectsToAccessDenied()
    {
        var sessionId = _data.AddSession("SV01");
        var orchestrator = new AIVES.BLL.Services.VivaInterview.VivaSessionOrchestrator(
            _data, _data, _data, _ai, _ai, _ai, _publisher, NullLogger<AIVES.BLL.Services.VivaInterview.VivaSessionOrchestrator>.Instance);

        var model = new ExamModel(NullLogger<ExamModel>.Instance, orchestrator)
        {
            PageContext = CreatePageContext(userId: "SV_OTHER_STUDENT")
        };

        var result = await model.OnGetAsync(sessionId);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Account/AccessDenied", redirect.PageName);
    }

    [Fact]
    public void ExamPage_CompletedCard_LinksPointToRazorPages()
    {
        var examViewPath = System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "StudentNameMVC", "Pages", "Viva", "Exam.cshtml");
        if (System.IO.File.Exists(examViewPath))
        {
            var content = System.IO.File.ReadAllText(examViewPath);
            Assert.Contains("asp-page=\"/StudentReports/Index\"", content);
            Assert.Contains("asp-page=\"/Account/Dashboard\"", content);
            Assert.DoesNotContain("asp-controller=\"StudentReports\"", content);
            Assert.DoesNotContain("asp-controller=\"Account\"", content);
        }
    }

    [Fact]
    public async Task ExamModel_OnGetAsync_WhenUserIsAuthorized_PopulatesModelAndReturnsPage()
    {
        var sessionId = _data.AddSession("SV01");
        var orchestrator = new AIVES.BLL.Services.VivaInterview.VivaSessionOrchestrator(
            _data, _data, _data, _ai, _ai, _ai, _publisher, NullLogger<AIVES.BLL.Services.VivaInterview.VivaSessionOrchestrator>.Instance);

        var model = new ExamModel(NullLogger<ExamModel>.Instance, orchestrator)
        {
            PageContext = CreatePageContext(userId: "SV01", userName: "Nguyen Van A")
        };

        var result = await model.OnGetAsync(sessionId);

        Assert.IsType<PageResult>(result);
        Assert.Equal(sessionId, model.SessionId);
        Assert.Equal("SV01", model.StudentId);
        Assert.Equal("Nguyen Van A", model.CandidateName);
        Assert.Equal("Sinh viên", model.CandidateRole);
    }

    [Fact]
    public async Task ExamModel_OnGetAsync_WithRealtimeService_PopulatesExactSnapshotFromDatabaseWithoutHardcoding()
    {
        var sessionId = _data.AddSession("SV99", VivaSessionState.QuestionDelivered);
        var session = _data.Sessions[sessionId];
        session.ExamId = "PRN222_VIVA";
        _data.Question.ExamId = "PRN222_VIVA";

        var orchestrator = new AIVES.BLL.Services.VivaInterview.VivaSessionOrchestrator(
            _data, _data, _data, _ai, _ai, _ai, _publisher, NullLogger<AIVES.BLL.Services.VivaInterview.VivaSessionOrchestrator>.Instance);
        var timerManager = new AIVES.BLL.Services.VivaInterview.VivaTimerManager(TimeProvider.System);
        var realtimeService = new AIVES.BLL.Services.VivaInterview.VivaRealtimeSessionService(
            _data, _data, timerManager, TimeProvider.System);

        var model = new ExamModel(NullLogger<ExamModel>.Instance, orchestrator, realtimeService)
        {
            PageContext = CreatePageContext(userId: "SV99", userName: "Tran Van Dynamic")
        };

        var result = await model.OnGetAsync(sessionId);

        Assert.IsType<PageResult>(result);
        Assert.Equal("PRN222_VIVA", model.ExamId);
        Assert.Equal("SV99", model.StudentId);
        Assert.Equal("Tran Van Dynamic", model.CandidateName);
        Assert.Equal(VivaSessionState.QuestionDelivered, model.SessionState);
        Assert.Equal("Q1", model.InitialQuestionId);
        Assert.Equal("Lifetime", model.InitialQuestionTitle);
        Assert.Equal("Explain scoped lifetime.", model.InitialQuestionContent);
    }

    [Fact]
    public async Task ExamModel_OnPostStartSessionAsync_WithEmptySessionId_ReturnsBadRequest()
    {
        var model = new ExamModel(NullLogger<ExamModel>.Instance)
        {
            PageContext = CreatePageContext()
        };

        var result = await model.OnPostStartSessionAsync(Guid.Empty);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task ExamModel_OnPostStartSessionAsync_WhenAuthorized_StartsSessionAndReturnsQuestion()
    {
        var sessionId = _data.AddSession("SV01", VivaSessionState.Ready);
        var orchestrator = new AIVES.BLL.Services.VivaInterview.VivaSessionOrchestrator(
            _data, _data, _data, _ai, _ai, _ai, _publisher, NullLogger<AIVES.BLL.Services.VivaInterview.VivaSessionOrchestrator>.Instance);

        var model = new ExamModel(NullLogger<ExamModel>.Instance, orchestrator)
        {
            PageContext = CreatePageContext(userId: "SV01")
        };

        var result = await model.OnPostStartSessionAsync(sessionId);

        var json = Assert.IsType<JsonResult>(result);
        Assert.NotNull(json.Value);
    }

    [Fact]
    public async Task ExamModel_OnPostSubmitAudioAsync_WithEmptyAudio_ReturnsBadRequest()
    {
        var model = new ExamModel(NullLogger<ExamModel>.Instance)
        {
            PageContext = CreatePageContext()
        };

        var result = await model.OnPostSubmitAudioAsync(Guid.NewGuid(), "Q1", null, 15);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task ExamModel_OnPostSubmitAudioAsync_WithOversizedAudio_ReturnsBadRequest()
    {
        var model = new ExamModel(NullLogger<ExamModel>.Instance)
        {
            PageContext = CreatePageContext()
        };

        var mockFile = new FormFile(new MemoryStream(new byte[1]), 0, 26 * 1024 * 1024, "audioFile", "test.webm");

        var result = await model.OnPostSubmitAudioAsync(Guid.NewGuid(), "Q1", mockFile, 15);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequest.Value);
    }

    [Fact]
    public async Task ExamModel_OnPostSubmitAudioAsync_WhenValid_ReturnsTranscriptJson()
    {
        var sessionId = _data.AddSession("SV01", VivaSessionState.QuestionDelivered);
        var orchestrator = new AIVES.BLL.Services.VivaInterview.VivaSessionOrchestrator(
            _data, _data, _data, _ai, _ai, _ai, _publisher, NullLogger<AIVES.BLL.Services.VivaInterview.VivaSessionOrchestrator>.Instance);

        var model = new ExamModel(NullLogger<ExamModel>.Instance, orchestrator)
        {
            PageContext = CreatePageContext(userId: "SV01")
        };

        var audioBytes = Encoding.UTF8.GetBytes("fake audio bytes");
        var formFile = new FormFile(new MemoryStream(audioBytes), 0, audioBytes.Length, "audioFile", "recording.webm")
        {
            Headers = new HeaderDictionary(),
            ContentType = "audio/webm"
        };

        var result = await model.OnPostSubmitAudioAsync(sessionId, "Q1", formFile, 10);

        var json = Assert.IsType<JsonResult>(result);
        Assert.NotNull(json.Value);
    }

    [Fact]
    public async Task ExamModel_OnPostSubmitAudioAsync_Success_GeneratesFollowUpForSubmittedTurn()
    {
        var stub = new StubOrchestrator();
        var model = new ExamModel(NullLogger<ExamModel>.Instance, stub)
        {
            PageContext = CreatePageContext(userId: "SV01")
        };

        var sessionId = Guid.NewGuid();
        var formFile = new FormFile(new MemoryStream(new byte[] { 1, 2, 3 }), 0, 3, "audioFile", "recording.webm")
        {
            Headers = new HeaderDictionary(),
            ContentType = "audio/webm"
        };

        var result = await model.OnPostSubmitAudioAsync(sessionId, "Q1", formFile, 15);

        var json = Assert.IsType<JsonResult>(result);
        Assert.NotNull(json.Value);
        Assert.Equal(sessionId, stub.CapturedFollowUpSessionId);
        Assert.Equal(stub.ExpectedTurnId, stub.CapturedFollowUpTurnId);
    }

    [Fact]
    public async Task ExamModel_OnPostSubmitAudioAsync_FollowUpFails_StillReturnsTranscript()
    {
        var stub = new StubOrchestrator
        {
            ShouldFailFollowUp = true
        };
        var model = new ExamModel(NullLogger<ExamModel>.Instance, stub)
        {
            PageContext = CreatePageContext(userId: "SV01")
        };

        var sessionId = Guid.NewGuid();
        var formFile = new FormFile(new MemoryStream(new byte[] { 1, 2, 3 }), 0, 3, "audioFile", "recording.webm")
        {
            Headers = new HeaderDictionary(),
            ContentType = "audio/webm"
        };

        var result = await model.OnPostSubmitAudioAsync(sessionId, "Q1", formFile, 15);

        var json = Assert.IsType<JsonResult>(result);
        Assert.NotNull(json.Value);
        var successProp = json.Value.GetType().GetProperty("success")?.GetValue(json.Value);
        Assert.Equal(true, successProp);
    }
}
