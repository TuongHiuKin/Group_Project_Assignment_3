using AIVES.BLL.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StudentNameMVC.ViewModels;

namespace StudentNameMVC.Controllers;

[Authorize(Roles = "Admin")]
public class ReportController : Controller
{
    private readonly IReportService _reportService;

    public ReportController(IReportService reportService)
    {
        _reportService = reportService ?? throw new ArgumentNullException(nameof(reportService));
    }

    [NonAction]
    [HttpGet]
    public async Task<IActionResult> Index(DateTime? startDate, DateTime? endDate, CancellationToken ct)
    {
        var nowVn = DateTime.UtcNow.AddHours(7);
        if (!startDate.HasValue && !endDate.HasValue)
        {
            startDate = new DateTime(nowVn.Year, nowVn.Month, 1);
            endDate = nowVn.Date;
        }

        var viewModel = new ReportIndexViewModel
        {
            StartDate = startDate,
            EndDate = endDate
        };

        if (!startDate.HasValue || !endDate.HasValue)
        {
            ModelState.AddModelError(string.Empty, "Vui lòng chọn đầy đủ cả Từ ngày và Đến ngày.");
            return View(viewModel);
        }

        if (startDate.Value.Date > endDate.Value.Date)
        {
            ModelState.AddModelError(string.Empty, "Từ ngày không được lớn hơn Đến ngày.");
            return View(viewModel);
        }

        try
        {
            var articles = await _reportService.GetReportArticlesAsync(startDate.Value, endDate.Value, ct);

            viewModel.Articles = articles.Select(a => new ReportItemViewModel
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

        return View(viewModel);
    }

    [NonAction]
    [HttpGet]
    public IActionResult AdminReport(DateTime? startDate, DateTime? endDate, CancellationToken ct)
    {
        return RedirectToAction(nameof(Index), new { startDate, endDate });
    }
}
