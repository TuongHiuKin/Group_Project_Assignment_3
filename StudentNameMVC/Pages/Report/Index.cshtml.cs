using AIVES.BLL.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using StudentNameMVC.ViewModels;

namespace StudentNameMVC.Pages.Report;

[Authorize(Roles = "Admin")]
public class IndexModel : PageModel
{
    private readonly IReportService _reportService;

    public IndexModel(IReportService reportService)
    {
        _reportService = reportService ?? throw new ArgumentNullException(nameof(reportService));
    }

    public ReportIndexViewModel ViewModel { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(DateTime? startDate, DateTime? endDate, CancellationToken ct = default)
    {
        var nowVn = DateTime.UtcNow.AddHours(7);
        if (!startDate.HasValue && !endDate.HasValue)
        {
            startDate = new DateTime(nowVn.Year, nowVn.Month, 1);
            endDate = nowVn.Date;
        }

        ViewModel = new ReportIndexViewModel
        {
            StartDate = startDate,
            EndDate = endDate
        };

        if (!startDate.HasValue || !endDate.HasValue)
        {
            ModelState.AddModelError(string.Empty, "Vui lòng chọn đầy đủ cả Từ ngày và Đến ngày.");
            return Page();
        }

        if (startDate.Value.Date > endDate.Value.Date)
        {
            ModelState.AddModelError(string.Empty, "Từ ngày không được lớn hơn Đến ngày.");
            return Page();
        }

        try
        {
            var articles = await _reportService.GetReportArticlesAsync(startDate.Value, endDate.Value, ct);

            ViewModel.Articles = articles.Select(a => new ReportItemViewModel
            {
                NewsArticleId = a.NewsArticleId,
                NewsTitle = a.NewsTitle,
                CategoryName = a.Category?.CategoryName ?? "Chưa phân loại",
                NewsStatus = a.NewsStatus,
                CreatedByName = a.CreatedBy?.AccountName ?? "N/A",
                CreatedDate = a.CreatedDate,
                TagNames = a.NewsTags?.Where(nt => nt.Tag != null).Select(nt => nt.Tag.TagName).ToList() ?? new List<string>()
            }).ToList();
        }
        catch (ArgumentException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
        }

        return Page();
    }
}
