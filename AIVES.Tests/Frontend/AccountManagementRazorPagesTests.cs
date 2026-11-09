using System.Reflection;
using AIVES.BLL.Interfaces;
using AIVES.BLL.Models;
using AIVES.BLL.Security;
using AIVES.DAL.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using StudentNameMVC.Pages.Account;
using StudentNameMVC.ViewModels;
using Xunit;

namespace AIVES.Tests.Frontend;

public sealed class AccountManagementRazorPagesTests
{
    [Fact]
    public void IndexModel_HasAdminRoleAuthorizationAttribute()
    {
        var auth = typeof(IndexModel).GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(auth);
        Assert.Equal(ApplicationRoles.Admin, auth.Roles);
    }

    [Fact]
    public async Task OnGetAsync_WithInvalidRoleFilter_AddsModelErrorAndClearsFilter()
    {
        var service = new StubAccountService();
        var model = CreatePageModel(service);

        var result = await model.OnGetAsync("test", roleFilter: 9);

        Assert.IsType<PageResult>(result);
        Assert.False(model.ModelState.IsValid);
        Assert.Null(model.ViewModel.RoleFilter);
    }

    [Fact]
    public async Task OnPostCreateAsync_WhenValid_CreatesAccountAndReturnsJsonSuccess()
    {
        var service = new StubAccountService();
        var model = CreatePageModel(service);

        var result = await model.OnPostCreateAsync(new AccountCreateViewModel
        {
            AccountName = "Nguyen Van Test",
            AccountEmail = "test@example.com",
            AccountPassword = "Password123",
            ConfirmPassword = "Password123",
            AccountRole = 1
        });

        var json = Assert.IsType<JsonResult>(result);
        Assert.Equal("Nguyen Van Test", service.CreatedName);
        Assert.NotNull(json.Value);
    }

    [Fact]
    public async Task OnPostHardDeleteAsync_WhenInUse_Returns400JsonError()
    {
        var service = new StubAccountService { HardDeleteResultToReturn = AccountDeleteResult.InUse };
        var model = CreatePageModel(service);

        var result = await model.OnPostHardDeleteAsync(5);

        Assert.Equal(400, model.Response.StatusCode);
        var json = Assert.IsType<JsonResult>(result);
        Assert.NotNull(json.Value);
    }

    [Fact]
    public async Task OnPostHardDeleteAsync_WhenDeleted_ReturnsJsonSuccess()
    {
        var service = new StubAccountService { HardDeleteResultToReturn = AccountDeleteResult.Deleted };
        var model = CreatePageModel(service);

        var result = await model.OnPostHardDeleteAsync(5);

        var json = Assert.IsType<JsonResult>(result);
        Assert.NotNull(json.Value);
        Assert.Equal("Xóa vĩnh viễn tài khoản thành công!", model.TempData["SuccessMessage"]);
    }

    private static IndexModel CreatePageModel(ISystemAccountService service)
    {
        var httpContext = new DefaultHttpContext();
        return new IndexModel(service)
        {
            PageContext = new PageContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new FakeTempDataProvider())
        };
    }

    private sealed class FakeTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private sealed class StubAccountService : ISystemAccountService
    {
        public string? CreatedName { get; private set; }
        public AccountDeleteResult HardDeleteResultToReturn { get; set; } = AccountDeleteResult.Deleted;

        public Task<List<SystemAccount>> SearchAccountsAsync(string? keyword = null, byte? role = null, bool includeDeleted = true, CancellationToken ct = default) =>
            Task.FromResult(new List<SystemAccount>
            {
                new() { AccountId = 1, AccountName = "SV 1", AccountEmail = "sv1@test.com", AccountRole = 1 }
            });

        public Task<SystemAccount?> GetAccountByIdAsync(int id, bool includeDeleted = false, CancellationToken ct = default) =>
            Task.FromResult<SystemAccount?>(new SystemAccount { AccountId = id, AccountName = "SV", AccountEmail = "sv@test.com", AccountRole = 1 });

        public Task<int> CreateAccountAsync(string name, string email, string password, byte role, CancellationToken ct = default)
        {
            CreatedName = name;
            return Task.FromResult(10);
        }

        public Task<int> RegisterStudentAsync(string name, string email, string password, int? defaultLecturerId = null, CancellationToken ct = default)
        {
            CreatedName = name;
            return Task.FromResult(10);
        }

        public Task<bool> UpdateAccountAsync(int id, string name, string email, byte role, string? newPassword = null, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<bool> SoftDeleteAccountAsync(int id, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<AccountDeleteResult> HardDeleteAccountAsync(int id, CancellationToken ct = default) =>
            Task.FromResult(HardDeleteResultToReturn);

        public Task<bool> IsAccountReferencedAsync(int id, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<bool> UpdateProfileAsync(int id, string name, string email, string? newPassword = null, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<SystemAccount?> AuthenticateAsync(string email, string password, CancellationToken ct = default) =>
            Task.FromResult<SystemAccount?>(null);



    }
}
