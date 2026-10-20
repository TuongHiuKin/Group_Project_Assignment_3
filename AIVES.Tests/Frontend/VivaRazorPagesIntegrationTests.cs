using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Models.VivaInterview;
using AIVES.BLL.Security;
using AIVES.DAL.Repositories.Interfaces;
using AIVES.Tests.Realtime;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AIVES.Tests.Frontend;

public sealed class VivaRazorPagesIntegrationTests : IClassFixture<VivaRazorPagesIntegrationTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<global::Program>
    {
        internal RealtimeData Data { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("VivaInterview:Enabled", "true");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["VivaInterview:Enabled"] = "true",
                ["ConnectionStrings:DefaultConnection"] = "Server=unused;Database=SignalRFixture;Integrated Security=true;TrustServerCertificate=true",
                ["DefaultAdmin:Email"] = "fixture@example.test",
                ["DefaultAdmin:Password"] = "fixture-only-not-a-real-account"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IVivaSessionRepository>(Data);
                services.AddSingleton<IVivaQuestionRepository>(Data);
                services.AddSingleton<IVivaTurnRepository>(Data);
                var ai = new RealtimeAi();
                services.AddSingleton<ISpeechToTextService>(ai);
                services.AddSingleton<ITextToSpeechService>(ai);
                services.AddSingleton<IFollowUpGeneratorService>(ai);

                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "FrontendFixture";
                    options.DefaultChallengeScheme = "FrontendFixture";
                }).AddScheme<AuthenticationSchemeOptions, FrontendTestAuth>("FrontendFixture", _ => { });

                services.AddLogging(options => options.SetMinimumLevel(LogLevel.Error));
            });
        }
    }

    private sealed class FrontendTestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var header = Request.Headers["X-Test-User"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(header)) return Task.FromResult(AuthenticateResult.NoResult());

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, header),
                new Claim(ClaimTypes.Name, $"Candidate {header}"),
                new Claim(ClaimTypes.Role, ApplicationRoles.Staff)
            };
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "FrontendFixture")), "FrontendFixture");
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = (int)HttpStatusCode.Redirect;
            Response.Headers.Location = "/Account/Login";
            return Task.CompletedTask;
        }

        protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = (int)HttpStatusCode.Redirect;
            Response.Headers.Location = "/Account/AccessDenied";
            return Task.CompletedTask;
        }
    }

    private readonly Factory _factory;

    public VivaRazorPagesIntegrationTests(Factory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Get_VivaIndex_WithoutAuth_RedirectsToLogin()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Viva");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Get_VivaIndex_WithAuth_ReturnsSuccessAndRendersPage()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "SV01");

        var response = await client.GetAsync("/Viva");

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Phòng thi Vấn đáp AI", html);
        Assert.Contains("Kiểm tra Micro", html);
        Assert.Contains("Tham gia phòng thi theo mã phiên", html);
        Assert.Contains("Về Dashboard", html);
    }

    [Fact]
    public async Task Get_VivaExam_WithoutSessionId_RedirectsToIndex()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-User", "SV01");

        var response = await client.GetAsync("/Viva/Exam");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Viva", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Get_VivaExam_WithAuthorizedStudent_ReturnsSuccessAndRendersRoom()
    {
        var sessionId = _factory.Data.AddSession("SV01", VivaSessionState.Ready);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "SV01");

        var response = await client.GetAsync($"/Viva/Exam/{sessionId}");

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains(sessionId.ToString(), html);
        Assert.Contains("visualizerCanvas", html);
        Assert.Contains("btnStartRecord", html);
        Assert.Contains("countdownTimerDisplay", html);
        Assert.Contains("transcriptCard", html);
    }

    [Fact]
    public async Task Get_VivaExam_WithUnauthorizedStudent_RedirectsToAccessDenied()
    {
        var sessionId = _factory.Data.AddSession("SV01", VivaSessionState.Ready);
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-User", "SV_OTHER_STUDENT");

        var response = await client.GetAsync($"/Viva/Exam/{sessionId}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("AccessDenied", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Get_VivaExam_RendersLeaveRoomModalWithTwoButtons()
    {
        var sessionId = _factory.Data.AddSession("SV01", VivaSessionState.Ready);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "SV01");

        var response = await client.GetAsync($"/Viva/Exam/{sessionId}");

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("id=\"leaveRoomModal\"", html);
        Assert.Contains("Ở lại phòng", html);
        Assert.Contains("id=\"btnConfirmLeaveRoom\"", html);
        Assert.Contains("href=\"/Account/Dashboard\"", html);
    }

    [Fact]
    public async Task Get_Layout_WithAuth_RendersUserDropdownAndRemovesSidebarSettings()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "SV01");

        var response = await client.GetAsync("/Account/StudentDashboard");

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // Dropdown toggle button with chevron & data-bs-toggle
        Assert.Contains("id=\"userProfileDropdown\"", html);
        Assert.Contains("data-bs-toggle=\"dropdown\"", html);
        Assert.Contains("bi-chevron-down", html);

        // Dropdown items
        Assert.Contains("/Account/Dashboard", html);
        Assert.Contains("Cài đặt", html);
        Assert.Contains("/Account/Logout", html);
        Assert.Contains("Đăng xuất", html);

        // Sidebar SETTINGS removed
        Assert.DoesNotContain("sidebar-category\">SETTINGS</div>", html);
    }
}
