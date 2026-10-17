using AIVES.BLL.Interfaces;
using AIVES.BLL.Options;
using AIVES.BLL.Security;
using AIVES.BLL.Services;
using AIVES.DAL;
using AIVES.DAL.Entities;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using StudentNameMVC.Security;
using StudentNameMVC;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();
builder.Services
    .AddOptions<AiServiceOptions>()
    .Bind(builder.Configuration.GetSection(AiServiceOptions.SectionName))
    .PostConfigure(options =>
    {
        if (string.IsNullOrWhiteSpace(options.TtsCacheDirectory) && builder.Environment.WebRootPath is not null)
            options.TtsCacheDirectory = Path.Combine(builder.Environment.WebRootPath, "audio", "cache");
        if (string.IsNullOrWhiteSpace(options.CredentialStorePath))
            options.CredentialStorePath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "ai-credentials.json");
    });
AIVES.BLL.DependencyInjection.AddAiCredentialManagement(builder.Services);

// Enable after DB/AI members register the real Viva dependencies. No production mocks are added.
var vivaEnabled = builder.Configuration.GetValue<bool>("VivaInterview:Enabled");
if (vivaEnabled)
{
    builder.Services.AddVivaRealtime();
    AIVES.BLL.DependencyInjection.AddVivaInterviewServices(builder.Services);
    AIVES.BLL.DependencyInjection.AddAiServices(builder.Services);
}
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required.");
builder.Services.AddAivesDataAccess(connectionString);

builder.Services
    .AddOptions<DefaultAdminOptions>()
    .Bind(builder.Configuration.GetSection(DefaultAdminOptions.SectionName))
    .Validate(
        options => !string.IsNullOrWhiteSpace(options.Email) &&
                   !string.IsNullOrWhiteSpace(options.Password),
        "DefaultAdmin:Email and DefaultAdmin:Password are required.")
    .ValidateOnStart();

// BLL Services
builder.Services.AddScoped<IPasswordHasher<SystemAccount>, PasswordHasher<SystemAccount>>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddDataProtection();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<PasswordResetEmailOptions>(builder.Configuration.GetSection("PasswordResetEmail"));
builder.Services.AddScoped<IPasswordResetEmailSender, PasswordResetEmailSender>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("password-reset", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
        }));
});
builder.Services.AddScoped<ISystemAccountService, SystemAccountService>();
builder.Services.AddScoped<ICategoryService, CategoryService>();
builder.Services.AddScoped<INewsArticleService, NewsArticleService>();
builder.Services.AddScoped<IReportService, ReportService>();
builder.Services.AddScoped<ActiveAccountCookieEvents>();

// Group 6 - Viva Exam Reporting Services
builder.Services.AddSingleton<AIVES.BLL.Interfaces.ExamReporting.IExamReportRepository, AIVES.BLL.Repositories.ExamReporting.MockExamReportRepository>();
builder.Services.AddScoped<AIVES.BLL.Interfaces.ExamReporting.IStudentReportService, AIVES.BLL.Services.ExamReporting.StudentReportService>();
builder.Services.AddScoped<AIVES.BLL.Interfaces.ExamReporting.IClassReportService, AIVES.BLL.Services.ExamReporting.ClassReportService>();

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "AIVES.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/AccessDenied";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.EventsType = typeof(ActiveAccountCookieEvents);
    });

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
    options.AddPolicy(AuthorizationPolicies.AdminOnly, policy =>
        policy.RequireRole(ApplicationRoles.Admin));
    options.AddPolicy(AuthorizationPolicies.StaffOnly, policy =>
        policy.RequireRole(ApplicationRoles.Staff));
    options.AddPolicy(AuthorizationPolicies.LecturerOnly, policy =>
        policy.RequireRole(ApplicationRoles.Lecturer));
});

var app = builder.Build();

if (vivaEnabled)
{
    var configured = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<AiServiceOptions>>().CurrentValue;
    var ai = AIVES.BLL.Services.Ai.AiSettingsResolver.Resolve(configured, app.Services.GetRequiredService<AIVES.BLL.Interfaces.Ai.IAiCredentialStore>());
    app.Logger.LogInformation("AI mode at startup: {Mode}", ai.UsesRealProviders ? "OpenAI (real providers)" : "Mock");
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
if (vivaEnabled) app.MapVivaRealtime();
app.MapRazorPages();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Account}/{action=Login}/{id?}");

app.Run();

// Entry point for WebApplicationFactory integration tests.
public partial class Program { }
