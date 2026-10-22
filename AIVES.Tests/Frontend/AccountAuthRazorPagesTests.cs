using System.IO;
using System.Reflection;
using System.Security.Claims;
using AIVES.BLL.Interfaces;
using AIVES.BLL.Interfaces.ExamReporting;
using AIVES.BLL.Models;
using AIVES.BLL.Models.ExamReporting;
using AIVES.BLL.Security;
using AIVES.DAL.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using StudentNameMVC.Pages.Account;
using StudentNameMVC.Security;
using Xunit;

namespace AIVES.Tests.Frontend;

public sealed class AccountAuthRazorPagesTests
{
    [Fact]
    public void Auth_and_dashboard_page_models_have_expected_security_attributes()
    {
        Assert.NotNull(typeof(LoginModel).GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.NotNull(typeof(ForgotPasswordModel).GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.NotNull(typeof(ResetPasswordModel).GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.NotNull(typeof(AccessDeniedModel).GetCustomAttribute<AllowAnonymousAttribute>());

        Assert.NotNull(typeof(LogoutModel).GetCustomAttribute<AuthorizeAttribute>());
        Assert.NotNull(typeof(DashboardModel).GetCustomAttribute<AuthorizeAttribute>());

        var adminAuth = typeof(AdminDashboardModel).GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(adminAuth);
        Assert.Equal(ApplicationRoles.Admin, adminAuth.Roles);

        var lecturerAuth = typeof(LecturerDashboardModel).GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(lecturerAuth);
        Assert.Equal(ApplicationRoles.Lecturer, lecturerAuth.Roles);

        var studentAuth = typeof(StudentDashboardModel).GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(studentAuth);
        Assert.Equal(ApplicationRoles.Staff, studentAuth.Roles);
    }

    [Fact]
    public async Task LoginModel_OnPostAsync_RedirectsAdminToAdminDashboard()
    {
        var adminUser = new AuthenticatedUser("1", "Admin", "admin@example.test", ApplicationRoles.Admin);
        var authService = new StubAuthService { AuthenticateResultToReturn = new AuthenticationResult(true, adminUser) };
        var model = CreateLoginModel(authService, out var authRecorder);
        model.Email = "admin@example.test";
        model.Password = "valid";

        var result = await model.OnPostAsync();

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Account/AdminDashboard", redirect.PageName);
        Assert.NotNull(authRecorder.SignedInPrincipal);
        Assert.True(authRecorder.SignedInPrincipal.IsInRole(ApplicationRoles.Admin));
    }

    [Fact]
    public async Task LoginModel_OnPostAsync_RedirectsLecturerToLecturerDashboard()
    {
        var lecturerUser = new AuthenticatedUser("2", "Lecturer", "lecturer@example.test", ApplicationRoles.Lecturer);
        var authService = new StubAuthService { AuthenticateResultToReturn = new AuthenticationResult(true, lecturerUser) };
        var model = CreateLoginModel(authService, out var authRecorder);
        model.Email = "lecturer@example.test";
        model.Password = "valid";

        var result = await model.OnPostAsync();

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Account/LecturerDashboard", redirect.PageName);
        Assert.NotNull(authRecorder.SignedInPrincipal);
        Assert.True(authRecorder.SignedInPrincipal.IsInRole(ApplicationRoles.Lecturer));
    }

    [Fact]
    public async Task LoginModel_OnPostAsync_RedirectsStaffToStudentDashboard()
    {
        var staffUser = new AuthenticatedUser("3", "Student", "student@example.test", ApplicationRoles.Staff);
        var authService = new StubAuthService { AuthenticateResultToReturn = new AuthenticationResult(true, staffUser) };
        var model = CreateLoginModel(authService, out var authRecorder);
        model.Email = "student@example.test";
        model.Password = "valid";

        var result = await model.OnPostAsync();

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Account/StudentDashboard", redirect.PageName);
        Assert.NotNull(authRecorder.SignedInPrincipal);
        Assert.True(authRecorder.SignedInPrincipal.IsInRole(ApplicationRoles.Staff));
    }

    [Fact]
    public async Task LoginModel_OnPostAsync_WithUnauthorizedReturnUrl_RedirectsToRoleDashboard()
    {
        var lecturerUser = new AuthenticatedUser("2", "Lecturer", "lecturer@example.test", ApplicationRoles.Lecturer);
        var authService = new StubAuthService { AuthenticateResultToReturn = new AuthenticationResult(true, lecturerUser) };
        var model = CreateLoginModel(authService, out _);
        model.Email = "lecturer@example.test";
        model.Password = "valid";
        model.ReturnUrl = "/Account/Index";

        var result = await model.OnPostAsync();

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Account/LecturerDashboard", redirect.PageName);
    }

    [Fact]
    public async Task LoginModel_OnPostAsync_WithValidReturnUrl_RedirectsToReturnUrl()
    {
        var staffUser = new AuthenticatedUser("3", "Student", "student@example.test", ApplicationRoles.Staff);
        var authService = new StubAuthService { AuthenticateResultToReturn = new AuthenticationResult(true, staffUser) };
        var model = CreateLoginModel(authService, out _);
        model.Email = "student@example.test";
        model.Password = "valid";
        model.ReturnUrl = "/Viva/Exam?sessionId=11111111-1111-1111-1111-111111111111";

        var result = await model.OnPostAsync();

        var redirect = Assert.IsType<LocalRedirectResult>(result);
        Assert.Equal("/Viva/Exam?sessionId=11111111-1111-1111-1111-111111111111", redirect.Url);
    }

    [Fact]
    public async Task LogoutModel_OnPostAsync_SignsOutAndRedirectsToLogin()
    {
        var authRecorder = new RecordingAuthenticationService();
        var services = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(authRecorder)
            .BuildServiceProvider();
        var model = new LogoutModel
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext { RequestServices = services } }
        };

        var result = await model.OnPostAsync();

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Account/Login", redirect.PageName);
        Assert.True(authRecorder.SignedOut);
    }

    [Theory]
    [InlineData(ApplicationRoles.Admin, "/Account/AdminDashboard")]
    [InlineData(ApplicationRoles.Lecturer, "/Account/LecturerDashboard")]
    [InlineData(ApplicationRoles.Staff, "/Account/StudentDashboard")]
    public void DashboardModel_OnGet_RedirectsByRole(string role, string expectedPage)
    {
        var principal = AuthenticationClaimsFactory.CreatePrincipal(new AuthenticatedUser("1", "Test", "test@example.test", role));
        var model = new DashboardModel
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext { User = principal } }
        };

        var result = model.OnGet();

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal(expectedPage, redirect.PageName);
    }

    [Fact]
    public async Task AdminDashboardModel_OnGetAsync_PopulatesRealCountsAndAccounts()
    {
        var accountService = new StubDashboardAccountService
        {
            Accounts = new List<SystemAccount>
            {
                new() { AccountId = 1, AccountName = "Admin", AccountEmail = "admin@test.com", AccountRole = 0 },
                new() { AccountId = 2, AccountName = "Lecturer A", AccountEmail = "gv@test.com", AccountRole = 2 },
                new() { AccountId = 3, AccountName = "Student A", AccountEmail = "sv@test.com", AccountRole = 1 }
            }
        };
        var reportService = new StubDashboardStudentReportService
        {
            Exams = new List<ExamSettingsSnapshot> { new() { ExamId = "EXAM01", ExamTitle = "Test Exam" } }
        };

        var model = new AdminDashboardModel(accountService, reportService);
        await model.OnGetAsync();

        Assert.Equal(3, model.TotalAccounts);
        Assert.Equal(1, model.TotalStudents);
        Assert.Equal(1, model.TotalLecturers);
        Assert.Equal(1, model.TotalExams);
        Assert.Equal(3, model.RecentAccounts.Count);
    }

    [Fact]
    public async Task AdminDashboardModel_OnGetAsync_WhenEmpty_HandlesSafely()
    {
        var accountService = new StubDashboardAccountService();
        var reportService = new StubDashboardStudentReportService();

        var model = new AdminDashboardModel(accountService, reportService);
        await model.OnGetAsync();

        Assert.Equal(0, model.TotalAccounts);
        Assert.Equal(0, model.TotalStudents);
        Assert.Equal(0, model.TotalLecturers);
        Assert.Equal(0, model.TotalExams);
        Assert.Empty(model.RecentAccounts);
    }

    [Fact]
    public async Task LecturerDashboardModel_OnGetAsync_PopulatesClassStatistics()
    {
        var classReportService = new StubDashboardClassReportService
        {
            Classes = new List<string> { "SE1701" },
            Exams = new List<ExamSettingsSnapshot> { new() { ExamId = "EXAM01", ExamTitle = "PRN222" } },
            Stats = new ClassStatisticsDto
            {
                ClassId = "SE1701",
                ExamId = "EXAM01",
                TotalStudentsCount = 25,
                AverageClassScore = 7.5m,
                HardestQuestions = new List<QuestionStatDto>
                {
                    new() { QuestionId = "Q1", QuestionTitle = "DI Lifetime", FailRate = 35.0m }
                }
            }
        };

        var model = new LecturerDashboardModel(classReportService)
        {
            PageContext = new PageContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, "GV01"),
                        new Claim(ClaimTypes.Role, ApplicationRoles.Lecturer)
                    }, "TestAuth"))
                }
            }
        };

        await model.OnGetAsync();

        Assert.Equal("SE1701", model.SelectedClassId);
        Assert.Equal("EXAM01", model.SelectedExamId);
        Assert.NotNull(model.ClassStats);
        Assert.Equal(25, model.ClassStats.TotalStudentsCount);
        Assert.Equal(7.5m, model.ClassStats.AverageClassScore);
    }

    [Fact]
    public async Task StudentDashboardModel_OnGetAsync_PopulatesStudentReportAndExams()
    {
        var studentReportService = new StubDashboardStudentReportService
        {
            Exams = new List<ExamSettingsSnapshot> { new() { ExamId = "EXAM01", ExamTitle = "PRN222 Viva" } },
            Report = new StudentReportDto
            {
                StudentId = "SV01",
                ExamId = "EXAM01",
                CompletedAttemptsCount = 1,
                MaxAllowedAttemptsCount = 3,
                PendingAttemptsCount = 1,
                GradedAttemptsCount = 0
            }
        };

        var model = new StudentDashboardModel(studentReportService)
        {
            PageContext = new PageContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, "SV01"),
                        new Claim(ClaimTypes.Role, ApplicationRoles.Staff)
                    }, "TestAuth"))
                }
            }
        };

        await model.OnGetAsync();

        Assert.Equal("EXAM01", model.SelectedExamId);
        Assert.Single(model.AvailableExams);
        Assert.NotNull(model.StudentReport);
        Assert.Equal(1, model.StudentReport.CompletedAttemptsCount);
        Assert.Equal(1, model.StudentReport.PendingAttemptsCount);
    }

    [Fact]
    public void Layout_And_Dashboards_DoNotContainUnsplashOrFakeExaminers()
    {
        var mvcPagesDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "StudentNameMVC", "Pages");
        if (Directory.Exists(mvcPagesDir))
        {
            var files = Directory.GetFiles(mvcPagesDir, "*.cshtml", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                var content = File.ReadAllText(file);
                Assert.DoesNotContain("images.unsplash.com", content);
                Assert.DoesNotContain("TS. Hoàng Yến", content);
                Assert.DoesNotContain("ThS. Minh Trí", content);
                Assert.DoesNotContain("TS. Lê Văn Hải", content);
            }
        }
    }

    private static LoginModel CreateLoginModel(IAuthService authService, out RecordingAuthenticationService authRecorder)
    {
        authRecorder = new RecordingAuthenticationService();
        var services = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(authRecorder)
            .BuildServiceProvider();

        var httpContext = new DefaultHttpContext { RequestServices = services };
        return new LoginModel(authService)
        {
            PageContext = new PageContext { HttpContext = httpContext },
            Url = new FakeUrlHelper()
        };
    }

    private sealed class FakeUrlHelper : IUrlHelper
    {
        public ActionContext ActionContext => throw new NotImplementedException();
        public string? Action(UrlActionContext actionContext) => null;
        public string? Content(string? contentPath) => contentPath;
        public bool IsLocalUrl(string? url) => !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\");
        public string? Link(string? routeName, object? values) => null;
        public string? RouteUrl(UrlRouteContext routeContext) => null;
    }

    private sealed class StubAuthService : IAuthService
    {
        public AuthenticationResult AuthenticateResultToReturn { get; set; } = AuthenticationResult.Failure;

        public Task<AuthenticationResult> AuthenticateAsync(string email, string password, CancellationToken cancellationToken = default) =>
            Task.FromResult(AuthenticateResultToReturn);

        public Task<string?> CreatePasswordResetTokenAsync(string email, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<bool> ResetPasswordAsync(string email, string token, string newPassword, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> IsAccountActiveAsync(int accountId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class RecordingAuthenticationService : IAuthenticationService
    {
        public bool SignedOut { get; private set; }
        public ClaimsPrincipal? SignedInPrincipal { get; private set; }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.NoResult());
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            SignedInPrincipal = principal;
            return Task.CompletedTask;
        }
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            SignedOut = true;
            return Task.CompletedTask;
        }
    }

    private sealed class StubDashboardAccountService : ISystemAccountService
    {
        public List<SystemAccount> Accounts { get; set; } = new();

        public Task<List<SystemAccount>> SearchAccountsAsync(string? keyword = null, byte? role = null, bool includeDeleted = true, CancellationToken ct = default)
            => Task.FromResult(Accounts);

        public Task<SystemAccount?> GetAccountByIdAsync(int id, bool includeDeleted = false, CancellationToken ct = default)
            => Task.FromResult(Accounts.FirstOrDefault(a => a.AccountId == id));

        public Task<int> CreateAccountAsync(string name, string email, string password, byte role, CancellationToken ct = default) => Task.FromResult(1);
        public Task<bool> UpdateAccountAsync(int id, string name, string email, byte role, string? newPassword = null, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SoftDeleteAccountAsync(int id, CancellationToken ct = default) => Task.FromResult(true);
        public Task<AccountDeleteResult> HardDeleteAccountAsync(int id, CancellationToken ct = default) => Task.FromResult(AccountDeleteResult.Deleted);
        public Task<bool> IsAccountReferencedAsync(int id, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> UpdateProfileAsync(int id, string name, string email, string? newPassword = null, CancellationToken ct = default) => Task.FromResult(true);
        public Task<SystemAccount?> AuthenticateAsync(string email, string password, CancellationToken ct = default) => Task.FromResult<SystemAccount?>(null);
        public Task<List<NewsArticle>> GetNewsArticlesFromUserAsync(int accountId, CancellationToken ct = default) => Task.FromResult(new List<NewsArticle>());
        public Task<List<NewsArticle>> GetNewsArticlesPublicAsync(CancellationToken ct = default) => Task.FromResult(new List<NewsArticle>());
        public Task<List<NewsArticle>> GetNewsArticlesLecturerAsync(int accountId, CancellationToken ct = default) => Task.FromResult(new List<NewsArticle>());
    }

    private sealed class StubDashboardStudentReportService : IStudentReportService
    {
        public List<ExamSettingsSnapshot> Exams { get; set; } = new();
        public StudentReportDto? Report { get; set; }

        public Task<List<ExamSettingsSnapshot>> GetAvailableExamsAsync(CancellationToken ct = default)
            => Task.FromResult(Exams);

        public Task<StudentReportDto?> GetStudentReportAsync(string studentId, string examId, CancellationToken ct = default)
            => Task.FromResult(Report);
    }

    private sealed class StubDashboardClassReportService : IClassReportService
    {
        public List<string> Classes { get; set; } = new();
        public List<ExamSettingsSnapshot> Exams { get; set; } = new();
        public ClassStatisticsDto? Stats { get; set; }

        public Task<List<string>> GetAvailableClassesForTeacherAsync(string teacherId, CancellationToken ct = default)
            => Task.FromResult(Classes);

        public Task<List<ExamSettingsSnapshot>> GetAvailableExamsAsync(CancellationToken ct = default)
            => Task.FromResult(Exams);

        public Task<ClassStatisticsDto?> GetClassStatisticsAsync(string teacherId, string classId, string examId, int? specificAttemptOrdinal = null, CancellationToken ct = default)
            => Task.FromResult(Stats);

        public Task<byte[]> ExportClassGradeSheetCsvAsync(string teacherId, string classId, string examId, CancellationToken ct = default)
            => Task.FromResult(Array.Empty<byte>());

        public Task<StudentReportDto?> GetStudentExamDetailForTeacherAsync(string teacherId, string classId, string examId, string studentId, CancellationToken ct = default)
            => Task.FromResult<StudentReportDto?>(null);
    }
}
