using System.Security.Claims;
using AIVES.BLL.Interfaces;
using AIVES.BLL.Interfaces.ExamReporting;
using AIVES.BLL.Models.ExamReporting;
using AIVES.BLL.Security;
using AIVES.DAL.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Xunit;
using ClassReportsIndexModel = StudentNameMVC.Pages.ClassReports.IndexModel;
using ClassReportsStudentDetailModel = StudentNameMVC.Pages.ClassReports.StudentDetailModel;
using ReportIndexModel = StudentNameMVC.Pages.Report.IndexModel;
using ReportAdminReportModel = StudentNameMVC.Pages.Report.AdminReportModel;
using StudentReportsIndexModel = StudentNameMVC.Pages.StudentReports.IndexModel;

namespace AIVES.Tests.Frontend;

public class ReportsRazorPagesTests
{
    private sealed class DummyTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private static void ConfigurePageContext(PageModel pageModel, ClaimsPrincipal? user = null)
    {
        var httpContext = new DefaultHttpContext();
        if (user != null)
        {
            httpContext.User = user;
        }

        var actionContext = new ActionContext(httpContext, new RouteData(), new PageActionDescriptor(), new ModelStateDictionary());
        pageModel.PageContext = new PageContext(actionContext)
        {
            ViewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), actionContext.ModelState)
        };
        pageModel.TempData = new TempDataDictionary(httpContext, new DummyTempDataProvider());
    }

    private sealed class StubClassReportService : IClassReportService
    {
        public bool ThrowUnauthorized { get; set; }

        public Task<ClassStatisticsDto?> GetClassStatisticsAsync(string teacherId, string classId, string examId, int? specificAttemptOrdinal = null, CancellationToken ct = default)
        {
            if (ThrowUnauthorized)
            {
                throw new UnauthorizedAccessException("Không có quyền truy cập lớp.");
            }

            return Task.FromResult<ClassStatisticsDto?>(new ClassStatisticsDto
            {
                ClassId = classId,
                ExamId = examId,
                ExamTitle = "Đề thi mẫu",
                TotalStudentsCount = 1,
                GradeExportRows = new List<ClassGradeExportRowDto>
                {
                    new() { StudentId = "10", StudentName = "Nguyễn Văn A", ClassId = classId, Attempt1Score = "Chưa chấm", AverageScore = "Chưa chấm", ResultStatus = "Chưa chấm" }
                }
            });
        }

        public Task<byte[]> ExportClassGradeSheetCsvAsync(string teacherId, string classId, string examId, CancellationToken ct = default) =>
            Task.FromResult(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'A' });

        public Task<StudentReportDto?> GetStudentExamDetailForTeacherAsync(string teacherId, string classId, string examId, string studentId, CancellationToken ct = default) =>
            Task.FromResult<StudentReportDto?>(new StudentReportDto
            {
                StudentId = studentId,
                StudentName = "Nguyễn Văn A",
                ExamId = examId,
                ExamTitle = "Đề thi mẫu",
                CompletedAttemptsCount = 1,
                MaxAllowedAttemptsCount = 3,
                PendingAttemptsCount = 1
            });

        public Task<List<ExamSettingsSnapshot>> GetAvailableExamsAsync(CancellationToken ct = default) =>
            Task.FromResult(new List<ExamSettingsSnapshot>
            {
                new() { ExamId = "EXAM01", ExamTitle = "Đề thi EXAM01" }
            });

        public Task<List<string>> GetAvailableClassesForTeacherAsync(string teacherId, CancellationToken ct = default) =>
            Task.FromResult(new List<string> { "EXAM01" });
    }

    private sealed class StubStudentReportService : IStudentReportService
    {
        public Task<StudentReportDto?> GetStudentReportAsync(string studentId, string examId, CancellationToken ct = default) =>
            Task.FromResult<StudentReportDto?>(new StudentReportDto
            {
                StudentId = studentId,
                StudentName = $"Student {studentId}",
                ExamId = examId,
                ExamTitle = "Đề thi EXAM01",
                CompletedAttemptsCount = 1,
                MaxAllowedAttemptsCount = 3,
                PendingAttemptsCount = 1
            });

        public Task<List<ExamSettingsSnapshot>> GetAvailableExamsAsync(CancellationToken ct = default) =>
            Task.FromResult(new List<ExamSettingsSnapshot>
            {
                new() { ExamId = "EXAM01", ExamTitle = "Đề thi EXAM01" }
            });
    }

    private sealed class StubReportService : IReportService
    {
        public Task<List<NewsArticle>> GetReportArticlesAsync(DateTime startDateLocal, DateTime endDateLocal, CancellationToken ct = default) =>
            Task.FromResult(new List<NewsArticle>
            {
                new() { NewsArticleId = 1, NewsTitle = "Bản tin 1", NewsStatus = 1, CreatedDate = DateTime.UtcNow }
            });

        public Task<List<NewsArticle>> GenerateReportAsync(DateTime startDate, DateTime endDate, CancellationToken ct = default) =>
            GetReportArticlesAsync(startDate, endDate, ct);

        public Task<List<NewsArticle>> GetNewsByCreatedDateRangeAsync(DateTime startUtc, DateTime endExclusiveUtc, CancellationToken ct = default) =>
            Task.FromResult(new List<NewsArticle>());
    }

    [Fact]
    public void ReportPageModels_HaveExpectedAuthorizeAttributesAndNoRepositoryDependency()
    {
        var classAuth = typeof(ClassReportsIndexModel).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>().FirstOrDefault();
        Assert.NotNull(classAuth);
        Assert.Contains(ApplicationRoles.Admin, classAuth!.Roles);
        Assert.Contains(ApplicationRoles.Lecturer, classAuth.Roles);

        var studentAuth = typeof(StudentReportsIndexModel).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>().FirstOrDefault();
        Assert.NotNull(studentAuth);
        Assert.Contains(ApplicationRoles.Admin, studentAuth!.Roles);
        Assert.Contains(ApplicationRoles.Staff, studentAuth.Roles);

        var reportAuth = typeof(ReportIndexModel).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>().FirstOrDefault();
        Assert.NotNull(reportAuth);
        Assert.Equal("Admin", reportAuth!.Roles);

        Assert.All(typeof(ClassReportsIndexModel).GetConstructors(), c =>
            Assert.DoesNotContain(c.GetParameters(), p => p.ParameterType == typeof(IExamReportRepository)));
        Assert.All(typeof(StudentReportsIndexModel).GetConstructors(), c =>
            Assert.DoesNotContain(c.GetParameters(), p => p.ParameterType == typeof(IExamReportRepository)));
    }

    [Fact]
    public async Task ClassReportsIndex_OnGetAsync_LoadsStatistics()
    {
        var service = new StubClassReportService();
        var pageModel = new ClassReportsIndexModel(service);
        ConfigurePageContext(pageModel);

        var result = await pageModel.OnGetAsync(examId: "EXAM01");

        Assert.IsType<PageResult>(result);
        Assert.NotNull(pageModel.Statistics);
        Assert.Equal("Chưa chấm", pageModel.Statistics!.GradeExportRows[0].ResultStatus);
    }

    [Fact]
    public async Task ClassReportsIndex_OnGetAsync_WhenUnauthorized_SetsTempDataError()
    {
        var service = new StubClassReportService { ThrowUnauthorized = true };
        var pageModel = new ClassReportsIndexModel(service);
        ConfigurePageContext(pageModel);

        var result = await pageModel.OnGetAsync(examId: "EXAM01");

        Assert.IsType<PageResult>(result);
        Assert.Null(pageModel.Statistics);
        Assert.NotNull(pageModel.TempData["ErrorMessage"]);
    }

    [Fact]
    public async Task ClassReportsIndex_OnGetExportGradeSheetAsync_ReturnsCsvFile()
    {
        var service = new StubClassReportService();
        var pageModel = new ClassReportsIndexModel(service);
        ConfigurePageContext(pageModel);

        var result = await pageModel.OnGetExportGradeSheetAsync(classId: "EXAM01", examId: "EXAM01");

        var fileResult = Assert.IsType<FileContentResult>(result);
        Assert.Equal("text/csv; charset=utf-8", fileResult.ContentType);
    }

    [Fact]
    public async Task ClassReportsStudentDetail_OnGetAsync_WithMissingParams_RedirectsToIndex()
    {
        var service = new StubClassReportService();
        var pageModel = new ClassReportsStudentDetailModel(service);
        ConfigurePageContext(pageModel);

        var result = await pageModel.OnGetAsync("", "EXAM01", "EXAM01");

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/ClassReports/Index", redirect.PageName);
    }

    [Fact]
    public async Task StudentReportsIndex_StudentCannotViewOtherStudentReport_ReturnsForbid()
    {
        var service = new StubStudentReportService();
        var pageModel = new StudentReportsIndexModel(service);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "SV01"),
            new Claim(ClaimTypes.Email, "student1@aives.test"),
            new Claim(ClaimTypes.Role, ApplicationRoles.Staff)
        }, "TestAuth"));
        ConfigurePageContext(pageModel, principal);

        var result = await pageModel.OnGetAsync(studentId: "SV02", examId: "EXAM01");

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task StudentReportsIndex_StudentCanViewOwnNumericIdReport_ReturnsPage()
    {
        var service = new StubStudentReportService();
        var pageModel = new StudentReportsIndexModel(service);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "10"),
            new Claim(ClaimTypes.Email, "student@fpt.edu.vn"),
            new Claim(ClaimTypes.Role, ApplicationRoles.Staff)
        }, "TestAuth"));
        ConfigurePageContext(pageModel, principal);

        var result = await pageModel.OnGetAsync(studentId: null, examId: "EXAM01");

        Assert.IsType<PageResult>(result);
        Assert.NotNull(pageModel.Report);
        Assert.Equal("10", pageModel.Report!.StudentId);
    }

    [Fact]
    public async Task ReportIndex_OnGetAsync_WithStartDateAfterEndDate_AddsModelError()
    {
        var service = new StubReportService();
        var pageModel = new ReportIndexModel(service);
        ConfigurePageContext(pageModel);

        var result = await pageModel.OnGetAsync(new DateTime(2026, 10, 20), new DateTime(2026, 10, 10));

        Assert.IsType<PageResult>(result);
        Assert.False(pageModel.ModelState.IsValid);
    }

    [Fact]
    public void ReportAdminReport_OnGet_RedirectsToReportIndex()
    {
        var pageModel = new ReportAdminReportModel();
        var result = pageModel.OnGet(new DateTime(2026, 10, 1), new DateTime(2026, 10, 20));

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Report/Index", redirect.PageName);
    }
}
