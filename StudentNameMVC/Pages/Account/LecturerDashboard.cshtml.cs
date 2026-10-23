using System.Security.Claims;
using AIVES.BLL.Interfaces.ExamReporting;
using AIVES.BLL.Models.ExamReporting;
using AIVES.BLL.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace StudentNameMVC.Pages.Account;

[Authorize(Roles = ApplicationRoles.Lecturer)]
public class LecturerDashboardModel : PageModel
{
    private readonly IClassReportService _classReportService;
    private readonly IRubricGradingService _rubricGradingService;

    public LecturerDashboardModel(IClassReportService classReportService, IRubricGradingService rubricGradingService)
    {
        _classReportService = classReportService ?? throw new ArgumentNullException(nameof(classReportService));
        _rubricGradingService = rubricGradingService ?? throw new ArgumentNullException(nameof(rubricGradingService));
    }

    public IReadOnlyList<QuestionRubric> Rubrics { get; set; } = Array.Empty<QuestionRubric>();
    public List<string> AvailableClasses { get; set; } = new();
    public List<ExamSettingsSnapshot> AvailableExams { get; set; } = new();
    public string? SelectedClassId { get; set; }
    public string? SelectedExamId { get; set; }
    public ClassStatisticsDto? ClassStats { get; set; }

    public async Task OnGetAsync(string? classId = null, string? examId = null, CancellationToken ct = default)
    {
        var teacherId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.Identity?.Name
            ?? "GV01";

        AvailableClasses = await _classReportService.GetAvailableClassesForTeacherAsync(teacherId, ct);
        AvailableExams = await _classReportService.GetAvailableExamsAsync(ct);

        SelectedClassId = !string.IsNullOrWhiteSpace(classId)
            ? classId
            : AvailableClasses.FirstOrDefault();

        SelectedExamId = !string.IsNullOrWhiteSpace(examId)
            ? examId
            : AvailableExams.FirstOrDefault()?.ExamId;

        Rubrics = _rubricGradingService.GetRubrics(SelectedExamId ?? "EXAM01");

        if (!string.IsNullOrWhiteSpace(SelectedClassId) && !string.IsNullOrWhiteSpace(SelectedExamId))
        {
            ClassStats = await _classReportService.GetClassStatisticsAsync(
                teacherId, SelectedClassId, SelectedExamId, null, ct);
        }
    }
}
