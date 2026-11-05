using AIVES.BLL.Interfaces.ExamReporting;
using AIVES.BLL.Interfaces.Grading;
using AIVES.BLL.Models.ExamReporting;
using AIVES.BLL.Models.Grading;
using AIVES.BLL.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using GradingState = AIVES.BLL.Models.Grading.GradingState;

namespace StudentNameMVC.Pages.Grading;

[Authorize(Roles = $"{ApplicationRoles.Lecturer},{ApplicationRoles.Admin}")]
public class IndexModel : PageModel
{
    private readonly IGradingOrchestrator _gradingOrchestrator;
    private readonly IClassReportService _classReportService;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        IGradingOrchestrator gradingOrchestrator,
        IClassReportService classReportService,
        ILogger<IndexModel> logger)
    {
        _gradingOrchestrator = gradingOrchestrator ?? throw new ArgumentNullException(nameof(gradingOrchestrator));
        _classReportService = classReportService ?? throw new ArgumentNullException(nameof(classReportService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [BindProperty(SupportsGet = true)]
    public string? SelectedExamId { get; set; }

    [BindProperty(SupportsGet = true)]
    public GradingState? SelectedState { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? SearchKeyword { get; set; }

    public List<ExamSettingsSnapshot> AvailableExams { get; set; } = new();
    public List<SessionGradingSummaryDto> Sessions { get; set; } = new();

    public int TotalSessionsCount { get; set; }
    public int PendingCount { get; set; }
    public int AiGradedCount { get; set; }
    public int FinalizedCount { get; set; }
    public int FailedCount { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct = default)
    {
        try
        {
            AvailableExams = await _classReportService.GetAvailableExamsAsync(ct);

            // Truy xuất danh sách phiên thi theo đề thi đã chọn (hoặc tất cả đề nếu rỗng)
            var allSessionsForExam = await _gradingOrchestrator.GetGradingListByExamAsync(
                SelectedExamId ?? string.Empty,
                null,
                null,
                ct);

            // Thống kê số lượng theo từng trạng thái chấm
            TotalSessionsCount = allSessionsForExam.Count;
            PendingCount = allSessionsForExam.Count(s => s.OverallState == GradingState.Pending);
            AiGradedCount = allSessionsForExam.Count(s => s.OverallState == GradingState.AiGraded);
            FinalizedCount = allSessionsForExam.Count(s => s.OverallState == GradingState.Finalized);
            FailedCount = allSessionsForExam.Count(s => s.OverallState == GradingState.Failed);

            // Lọc theo trạng thái chấm nếu được chọn
            var filtered = allSessionsForExam.AsEnumerable();
            if (SelectedState.HasValue)
            {
                filtered = filtered.Where(s => s.OverallState == SelectedState.Value);
            }

            // Lọc theo từ khóa tìm kiếm (MSSV, Họ tên, Mã đề)
            if (!string.IsNullOrWhiteSpace(SearchKeyword))
            {
                var kw = SearchKeyword.Trim();
                filtered = filtered.Where(s =>
                    (!string.IsNullOrWhiteSpace(s.StudentId) && s.StudentId.Contains(kw, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(s.StudentName) && s.StudentName.Contains(kw, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(s.ExamId) && s.ExamId.Contains(kw, StringComparison.OrdinalIgnoreCase)));
            }

            Sessions = filtered
                .OrderByDescending(s => s.SessionCompletedAtUtc)
                .ToList();

            return Page();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi khi tải danh sách bài thi chấm tại /Grading/Index");
            TempData["ErrorMessage"] = "Không thể tải danh sách bài thi. Vui lòng thử lại sau.";
            Sessions = new();
            return Page();
        }
    }

    public string GetExamTitle(string examId)
    {
        if (string.IsNullOrWhiteSpace(examId)) return "—";
        var exam = AvailableExams.FirstOrDefault(e => string.Equals(e.ExamId, examId, StringComparison.OrdinalIgnoreCase));
        return exam != null ? $"{exam.ExamTitle} ({exam.ExamId})" : examId;
    }
}
