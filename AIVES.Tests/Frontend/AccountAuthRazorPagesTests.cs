using System.Reflection;
using System.Security.Claims;
using AIVES.BLL.Interfaces;
using AIVES.BLL.Models;
using AIVES.BLL.Security;
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
}
