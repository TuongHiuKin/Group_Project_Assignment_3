using System.Security.Claims;
using AIVES.BLL.Interfaces;
using AIVES.DAL.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using StudentNameMVC.Pages.NewsArticle;
using StudentNameMVC.ViewModels;
using Xunit;

namespace AIVES.Tests.Frontend;

public class NewsArticleRazorPagesTests
{
    private sealed class DummyTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private sealed class FakeNewsArticleService : INewsArticleService
    {
        public int LastCreatedById { get; private set; }
        public int LastUpdatedById { get; private set; }
        public bool CreateCalled { get; private set; }
        public bool UpdateCalled { get; private set; }
        public bool DeleteCalled { get; private set; }

        public Task<int> CreateNewsAsync(string title, string content, int categoryId, byte status, int createdById, IReadOnlyCollection<int> tagIds, CancellationToken ct = default)
        {
            CreateCalled = true;
            LastCreatedById = createdById;
            return Task.FromResult(100);
        }

        public Task<bool> UpdateNewsAsync(int id, string title, string content, int categoryId, byte status, int updatedById, IReadOnlyCollection<int> tagIds, CancellationToken ct = default)
        {
            UpdateCalled = true;
            LastUpdatedById = updatedById;
            return Task.FromResult(true);
        }

        public Task<bool> DeleteNewsAsync(int id, CancellationToken ct = default)
        {
            DeleteCalled = true;
            return Task.FromResult(true);
        }

        public Task<NewsArticle?> GetNewsByIdAsync(int id, CancellationToken ct = default) =>
            Task.FromResult<NewsArticle?>(new NewsArticle { NewsArticleId = id, NewsTitle = "Sample", NewsContent = "Body", CategoryId = 1, NewsStatus = 1 });

        public Task<List<NewsArticle>> SearchNewsAsync(string? keyword = null, CancellationToken ct = default) =>
            Task.FromResult(new List<NewsArticle>
            {
                new() { NewsArticleId = 1, NewsTitle = "Article 1", NewsContent = "Content 1", CategoryId = 1, NewsStatus = 1, CreatedDate = DateTime.UtcNow }
            });

        public Task<List<Category>> GetCategoriesForDropdownAsync(CancellationToken ct = default) => Task.FromResult(new List<Category>());
        public Task<List<Tag>> GetAllTagsAsync(CancellationToken ct = default) => Task.FromResult(new List<Tag>());
        public Task<List<NewsArticle>> GetActiveNewsAsync(string? keyword = null, CancellationToken ct = default) => Task.FromResult(new List<NewsArticle>());
        public Task<List<NewsArticle>> GetNewsByCreatorAsync(int creatorId, string? keyword = null, CancellationToken ct = default) => Task.FromResult(new List<NewsArticle>());
        public Task<List<NewsArticle>> GetNewsByCreatedDateRangeAsync(DateTime startUtc, DateTime endExclusiveUtc, CancellationToken ct = default) => Task.FromResult(new List<NewsArticle>());
    }

    private static IndexModel CreatePageModel(FakeNewsArticleService service, ClaimsPrincipal? user)
    {
        var pageModel = new IndexModel(service);
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
        return pageModel;
    }

    [Fact]
    public void IndexModel_IsDecoratedWithAuthorizeForStaff()
    {
        var authAttr = typeof(IndexModel)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .FirstOrDefault();

        Assert.NotNull(authAttr);
        Assert.NotNull(authAttr.Roles);
        Assert.Contains("1", authAttr.Roles);
        Assert.Contains("Staff", authAttr.Roles);
    }

    [Fact]
    public async Task OnGetAsync_LoadsArticlesAndKeyword()
    {
        var service = new FakeNewsArticleService();
        var pageModel = CreatePageModel(service, null);

        var result = await pageModel.OnGetAsync("AI");

        Assert.IsType<PageResult>(result);
        Assert.Equal("AI", pageModel.ViewModel.Keyword);
        Assert.Single(pageModel.ViewModel.Articles);
    }

    [Fact]
    public async Task OnPostCreateAsync_WithUnauthenticatedUser_ReturnsChallenge()
    {
        var service = new FakeNewsArticleService();
        var unauthenticatedUser = new ClaimsPrincipal(new ClaimsIdentity());
        var pageModel = CreatePageModel(service, unauthenticatedUser);

        var model = new NewsArticleFormViewModel
        {
            NewsTitle = "Test Title",
            NewsContent = "Test Content",
            CategoryId = 1,
            NewsStatus = 1
        };

        var result = await pageModel.OnPostCreateAsync(model);

        Assert.IsType<ChallengeResult>(result);
        Assert.False(service.CreateCalled);
    }

    [Fact]
    public async Task OnPostCreateAsync_WithValidAuthenticatedStaff_CallsServiceAndReturnsJson()
    {
        var service = new FakeNewsArticleService();
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "42"),
            new Claim(ClaimTypes.Role, "Staff")
        };
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        var pageModel = CreatePageModel(service, user);

        var model = new NewsArticleFormViewModel
        {
            NewsTitle = "Test Title",
            NewsContent = "Test Content",
            CategoryId = 1,
            NewsStatus = 1
        };

        var result = await pageModel.OnPostCreateAsync(model);

        Assert.IsType<JsonResult>(result);
        Assert.True(service.CreateCalled);
        Assert.Equal(42, service.LastCreatedById);
    }

    [Fact]
    public async Task OnPostEditAsync_WithValidAuthenticatedStaff_CallsServiceAndReturnsJson()
    {
        var service = new FakeNewsArticleService();
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "77"),
            new Claim(ClaimTypes.Role, "Staff")
        };
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        var pageModel = CreatePageModel(service, user);

        var model = new NewsArticleFormViewModel
        {
            NewsArticleId = 10,
            NewsTitle = "Edit Title",
            NewsContent = "Edit Content",
            CategoryId = 1,
            NewsStatus = 1
        };

        var result = await pageModel.OnPostEditAsync(10, model);

        Assert.IsType<JsonResult>(result);
        Assert.True(service.UpdateCalled);
        Assert.Equal(77, service.LastUpdatedById);
    }

    [Fact]
    public async Task OnPostDeleteAsync_WithValidId_CallsServiceAndReturnsJson()
    {
        var service = new FakeNewsArticleService();
        var pageModel = CreatePageModel(service, null);

        var result = await pageModel.OnPostDeleteAsync(5);

        Assert.IsType<JsonResult>(result);
        Assert.True(service.DeleteCalled);
    }
}
