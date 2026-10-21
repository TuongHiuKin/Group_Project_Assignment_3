using AIVES.BLL.Interfaces;
using AIVES.BLL.Models;
using AIVES.BLL.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using StudentNameMVC.ViewModels;

namespace StudentNameMVC.Pages.Account;

[Authorize(Roles = ApplicationRoles.Admin)]
public class IndexModel : PageModel
{
    private readonly ISystemAccountService _accountService;

    public IndexModel(ISystemAccountService accountService)
    {
        _accountService = accountService ?? throw new ArgumentNullException(nameof(accountService));
    }

    public AccountIndexViewModel ViewModel { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(
        string? keyword = null,
        byte? roleFilter = null,
        CancellationToken ct = default)
    {
        if (roleFilter.HasValue && roleFilter.Value is not (1 or 2))
        {
            ModelState.AddModelError(nameof(roleFilter), "Vai trò lọc không hợp lệ. Chỉ chấp nhận Sinh viên (1) hoặc Giảng viên (2).");
            roleFilter = null;
        }

        var accounts = await _accountService.SearchAccountsAsync(keyword, roleFilter, includeDeleted: true, ct);

        var items = new List<AccountItemViewModel>();
        foreach (var a in accounts)
        {
            var isReferenced = await _accountService.IsAccountReferencedAsync(a.AccountId, ct);
            items.Add(new AccountItemViewModel
            {
                AccountId = a.AccountId,
                AccountName = a.AccountName,
                AccountEmail = a.AccountEmail,
                AccountRole = a.AccountRole,
                IsDeleted = a.IsDeleted,
                IsReferenced = isReferenced,
                StudentCode = null,
                ClassName = null
            });
        }

        ViewModel = new AccountIndexViewModel
        {
            Keyword = keyword,
            RoleFilter = roleFilter,
            Accounts = items
        };

        return Page();
    }

    public IActionResult OnGetCreateModal()
    {
        return Partial("_CreateModal", new AccountCreateViewModel());
    }

    public async Task<IActionResult> OnPostCreateAsync(
        [FromForm] AccountCreateViewModel model,
        CancellationToken ct = default)
    {
        if (!ModelState.IsValid)
        {
            Response.StatusCode = 400;
            return Partial("_CreateModal", model);
        }

        try
        {
            await _accountService.CreateAccountAsync(
                model.AccountName,
                model.AccountEmail,
                model.AccountPassword,
                model.AccountRole,
                ct);

            TempData["SuccessMessage"] = "Tạo mới tài khoản thành công!";
            return new JsonResult(new { success = true, redirectUrl = "/Account/Index" });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            Response.StatusCode = 400;
            return Partial("_CreateModal", model);
        }
    }

    public async Task<IActionResult> OnGetEditModalAsync(
        int id,
        CancellationToken ct = default)
    {
        if (id <= 0) return NotFound();

        var account = await _accountService.GetAccountByIdAsync(id, includeDeleted: true, ct);
        if (account == null) return NotFound();

        var model = new AccountEditViewModel
        {
            AccountId = account.AccountId,
            AccountName = account.AccountName,
            AccountEmail = account.AccountEmail,
            AccountRole = account.AccountRole
        };

        return Partial("_EditModal", model);
    }

    public async Task<IActionResult> OnPostEditAsync(
        int id,
        [FromForm] AccountEditViewModel model,
        CancellationToken ct = default)
    {
        if (id != model.AccountId) return BadRequest();

        if (!ModelState.IsValid)
        {
            Response.StatusCode = 400;
            return Partial("_EditModal", model);
        }

        try
        {
            var updated = await _accountService.UpdateAccountAsync(
                id,
                model.AccountName,
                model.AccountEmail,
                model.AccountRole,
                model.NewPassword,
                ct);

            if (!updated)
            {
                return NotFound();
            }

            TempData["SuccessMessage"] = "Cập nhật tài khoản thành công!";
            return new JsonResult(new { success = true, redirectUrl = "/Account/Index" });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            Response.StatusCode = 400;
            return Partial("_EditModal", model);
        }
    }

    public async Task<IActionResult> OnGetDeleteModalAsync(
        int id,
        CancellationToken ct = default)
    {
        if (id <= 0) return NotFound();

        var account = await _accountService.GetAccountByIdAsync(id, includeDeleted: true, ct);
        if (account == null) return NotFound();

        var model = new AccountDeleteViewModel
        {
            AccountId = account.AccountId,
            AccountName = account.AccountName,
            AccountEmail = account.AccountEmail,
            RoleName = account.AccountRole == 1 ? "Sinh viên" : "Giảng viên",
            IsHardDelete = false
        };

        return Partial("_DeleteModal", model);
    }

    public async Task<IActionResult> OnPostDeleteAsync(
        int id,
        CancellationToken ct = default)
    {
        if (id <= 0) return BadRequest();

        var deleted = await _accountService.SoftDeleteAccountAsync(id, ct);
        if (!deleted)
        {
            return NotFound();
        }

        TempData["SuccessMessage"] = "Khóa tài khoản thành công!";
        return new JsonResult(new { success = true, redirectUrl = "/Account/Index" });
    }

    public async Task<IActionResult> OnGetHardDeleteModalAsync(
        int id,
        CancellationToken ct = default)
    {
        if (id <= 0) return NotFound();

        var account = await _accountService.GetAccountByIdAsync(id, includeDeleted: true, ct);
        if (account == null) return NotFound();

        var isReferenced = await _accountService.IsAccountReferencedAsync(id, ct);

        var model = new AccountDeleteViewModel
        {
            AccountId = account.AccountId,
            AccountName = account.AccountName,
            AccountEmail = account.AccountEmail,
            RoleName = account.AccountRole == 1 ? "Sinh viên" : "Giảng viên",
            IsHardDelete = true,
            IsReferenced = isReferenced
        };

        return Partial("_HardDeleteModal", model);
    }

    public async Task<IActionResult> OnPostHardDeleteAsync(
        int id,
        CancellationToken ct = default)
    {
        if (id <= 0) return BadRequest();

        var result = await _accountService.HardDeleteAccountAsync(id, ct);
        if (result == AccountDeleteResult.InUse)
        {
            Response.StatusCode = 400;
            return new JsonResult(new
            {
                success = false,
                message = "Không thể xóa vĩnh viễn tài khoản này vì đã có bài viết tin tức tham chiếu (là Tác giả hoặc Người chỉnh sửa gần nhất)."
            });
        }

        if (result == AccountDeleteResult.NotFound)
        {
            return NotFound();
        }

        TempData["SuccessMessage"] = "Xóa vĩnh viễn tài khoản thành công!";
        return new JsonResult(new { success = true, redirectUrl = "/Account/Index" });
    }
}
