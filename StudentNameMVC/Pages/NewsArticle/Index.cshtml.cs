using System.Security.Claims;
using AIVES.BLL.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using StudentNameMVC.ViewModels;

namespace StudentNameMVC.Pages.NewsArticle;

[Authorize(Roles = "1,Staff")]
public class IndexModel : PageModel
{
    private readonly INewsArticleService _newsService;

    public IndexModel(INewsArticleService newsService)
    {
        _newsService = newsService ?? throw new ArgumentNullException(nameof(newsService));
    }

    public NewsArticleIndexViewModel ViewModel { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(string? keyword)
    {
        var articles = await _newsService.SearchNewsAsync(keyword);

        ViewModel = new NewsArticleIndexViewModel
        {
            Keyword = keyword,
            Articles = articles.Select(a => new NewsArticleItemViewModel
            {
                NewsArticleId = a.NewsArticleId,
                NewsTitle = a.NewsTitle,
                NewsContent = a.NewsContent,
                CategoryId = a.CategoryId,
                CategoryName = a.Category?.CategoryName ?? "Chưa phân loại",
                NewsStatus = a.NewsStatus,
                CreatedById = a.CreatedById,
                CreatedByName = a.CreatedBy?.AccountName ?? "N/A",
                CreatedDate = a.CreatedDate,
                UpdatedById = a.UpdatedById,
                UpdatedByName = a.UpdatedBy?.AccountName,
                ModifiedDate = a.ModifiedDate,
                TagNames = a.NewsTags?
                    .Where(nt => nt.Tag != null)
                    .Select(nt => nt.Tag.TagName)
                    .ToList() ?? new List<string>()
            }).ToList()
        };

        return Page();
    }

    public async Task<IActionResult> OnGetCreateModalAsync()
    {
        var model = new NewsArticleFormViewModel
        {
            NewsStatus = 1
        };
        await PopulateDropdownsAsync(model);
        return Partial("_CreateModal", model);
    }

    public async Task<IActionResult> OnPostCreateAsync(NewsArticleFormViewModel model)
    {
        var currentUserId = GetCurrentUserId();
        if (currentUserId == null)
        {
            return Challenge();
        }

        if (!ModelState.IsValid)
        {
            await PopulateDropdownsAsync(model);
            Response.StatusCode = 400;
            return Partial("_CreateModal", model);
        }

        try
        {
            await _newsService.CreateNewsAsync(
                model.NewsTitle,
                model.NewsContent,
                model.CategoryId,
                model.NewsStatus,
                currentUserId.Value,
                model.SelectedTagIds
            );

            TempData["SuccessMessage"] = "Tạo mới bài viết tin tức thành công!";
            return new JsonResult(new { success = true, redirectUrl = "/NewsArticle" });
        }
        catch (ArgumentException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            await PopulateDropdownsAsync(model);
            Response.StatusCode = 400;
            return Partial("_CreateModal", model);
        }
    }

    public async Task<IActionResult> OnGetEditModalAsync(int id)
    {
        if (id <= 0) return NotFound();

        var article = await _newsService.GetNewsByIdAsync(id);
        if (article == null) return NotFound();

        var model = new NewsArticleFormViewModel
        {
            NewsArticleId = article.NewsArticleId,
            NewsTitle = article.NewsTitle,
            NewsContent = article.NewsContent,
            CategoryId = article.CategoryId,
            NewsStatus = article.NewsStatus,
            SelectedTagIds = article.NewsTags?.Select(nt => nt.TagId).ToList() ?? new List<int>()
        };

        await PopulateDropdownsAsync(model);
        return Partial("_EditModal", model);
    }

    public async Task<IActionResult> OnPostEditAsync(int id, NewsArticleFormViewModel model)
    {
        if (id != model.NewsArticleId) return BadRequest();

        var currentUserId = GetCurrentUserId();
        if (currentUserId == null)
        {
            return Challenge();
        }

        if (!ModelState.IsValid)
        {
            await PopulateDropdownsAsync(model);
            Response.StatusCode = 400;
            return Partial("_EditModal", model);
        }

        try
        {
            var updated = await _newsService.UpdateNewsAsync(
                id,
                model.NewsTitle,
                model.NewsContent,
                model.CategoryId,
                model.NewsStatus,
                currentUserId.Value,
                model.SelectedTagIds
            );

            if (!updated)
            {
                return NotFound();
            }

            TempData["SuccessMessage"] = "Cập nhật bài viết thành công!";
            return new JsonResult(new { success = true, redirectUrl = "/NewsArticle" });
        }
        catch (ArgumentException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            await PopulateDropdownsAsync(model);
            Response.StatusCode = 400;
            return Partial("_EditModal", model);
        }
    }

    public async Task<IActionResult> OnGetDeleteModalAsync(int id)
    {
        if (id <= 0) return NotFound();

        var article = await _newsService.GetNewsByIdAsync(id);
        if (article == null) return NotFound();

        var model = new NewsArticleDeleteViewModel
        {
            NewsArticleId = article.NewsArticleId,
            NewsTitle = article.NewsTitle
        };

        return Partial("_DeleteModal", model);
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        if (id <= 0) return BadRequest();

        var deleted = await _newsService.DeleteNewsAsync(id);
        if (!deleted)
        {
            return NotFound();
        }

        TempData["SuccessMessage"] = "Xóa bài viết thành công!";
        return new JsonResult(new { success = true, redirectUrl = "/NewsArticle" });
    }

    private int? GetCurrentUserId()
    {
        if (User?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var claimVal = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (int.TryParse(claimVal, out var id) && id > 0)
        {
            return id;
        }

        return null;
    }

    private async Task PopulateDropdownsAsync(NewsArticleFormViewModel model)
    {
        var categories = await _newsService.GetCategoriesForDropdownAsync();
        model.CategoryOptions = categories.Select(c => new SelectListItem
        {
            Value = c.CategoryId.ToString(),
            Text = c.CategoryName,
            Selected = c.CategoryId == model.CategoryId
        }).ToList();

        var tags = await _newsService.GetAllTagsAsync();
        model.TagOptions = tags.Select(t => new SelectListItem
        {
            Value = t.TagId.ToString(),
            Text = t.TagName,
            Selected = model.SelectedTagIds.Contains(t.TagId)
        }).ToList();
    }
}
