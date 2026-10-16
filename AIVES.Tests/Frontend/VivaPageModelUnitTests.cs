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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StudentNameMVC.Pages.Viva;
using Xunit;

namespace AIVES.Tests.Frontend;

public sealed class VivaPageModelUnitTests
{
    private readonly RealtimeData _data = new();
    private readonly RealtimeAi _ai = new();
    private readonly RecordingPublisher _publisher = new();

    private IServiceProvider CreateServiceProvider(IVivaSessionOrchestrator? orchestrator = null, IVivaRealtimeSessionService? realtime = null)
    {
        var services = new ServiceCollection();
        if (orchestrator != null)
        {
            services.AddSingleton(orchestrator);
        }
        if (realtime != null)
        {
            services.AddSingleton(realtime);
        }
        return services.BuildServiceProvider();
    }

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

        public Task<VivaSessionStatusDto> InitializeSessionAsync(string examId, string studentId, CancellationToken ct = default)
        {
            CapturedExamId = examId;
            CapturedStudentId = studentId;
            return Task.FromResult(new VivaSessionStatusDto(ExpectedSessionId, examId, studentId, VivaSessionState.Created, 0, 2, DateTime.UtcNow, null));
        }

        public Task<bool> AuthorizeUserForSessionAsync(Guid sessionId, string userId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<VivaQuestionDto> StartSessionAsync(Guid sessionId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<VivaTranscriptDto> SubmitAnswerAudioAsync(VivaAnswerSubmissionDto submission, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<VivaFollowUpDto?> GenerateFollowUpAsync(Guid sessionId, Guid turnId, CancellationToken ct = default) => throw new NotImplementedException();
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
        var sp = CreateServiceProvider(stub);
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) };
        var model = new IndexModel(NullLogger<IndexModel>.Instance, sp)
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
        var sp = CreateServiceProvider(stub);
        var model = new IndexModel(NullLogger<IndexModel>.Instance, sp)
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

    [Fact]
    public async Task ExamModel_OnGetAsync_WithNullOrEmptySessionId_RedirectsToIndex()
    {
        var sp = CreateServiceProvider();
        var model = new ExamModel(NullLogger<ExamModel>.Instance, sp)
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

        var sp = CreateServiceProvider(orchestrator);
        var model = new ExamModel(NullLogger<ExamModel>.Instance, sp)
        {
            PageContext = CreatePageContext(userId: "SV_OTHER_STUDENT")
        };

        var result = await model.OnGetAsync(sessionId);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("AccessDenied", redirect.ActionName);
        Assert.Equal("Account", redirect.ControllerName);
    }

    [Fact]
    public async Task ExamModel_OnGetAsync_WhenUserIsAuthorized_PopulatesModelAndReturnsPage()
    {
        var sessionId = _data.AddSession("SV01");
        var orchestrator = new AIVES.BLL.Services.VivaInterview.VivaSessionOrchestrator(
            _data, _data, _data, _ai, _ai, _ai, _publisher, NullLogger<AIVES.BLL.Services.VivaInterview.VivaSessionOrchestrator>.Instance);

        var sp = CreateServiceProvider(orchestrator);
        var model = new ExamModel(NullLogger<ExamModel>.Instance, sp)
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

        var sp = CreateServiceProvider(orchestrator, realtimeService);
        var model = new ExamModel(NullLogger<ExamModel>.Instance, sp)
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
        var sp = CreateServiceProvider();
        var model = new ExamModel(NullLogger<ExamModel>.Instance, sp)
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

        var sp = CreateServiceProvider(orchestrator);
        var model = new ExamModel(NullLogger<ExamModel>.Instance, sp)
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
        var sp = CreateServiceProvider();
        var model = new ExamModel(NullLogger<ExamModel>.Instance, sp)
        {
            PageContext = CreatePageContext()
        };

        var result = await model.OnPostSubmitAudioAsync(Guid.NewGuid(), "Q1", null, 15);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task ExamModel_OnPostSubmitAudioAsync_WithOversizedAudio_ReturnsBadRequest()
    {
        var sp = CreateServiceProvider();
        var model = new ExamModel(NullLogger<ExamModel>.Instance, sp)
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

        var sp = CreateServiceProvider(orchestrator);
        var model = new ExamModel(NullLogger<ExamModel>.Instance, sp)
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
}
