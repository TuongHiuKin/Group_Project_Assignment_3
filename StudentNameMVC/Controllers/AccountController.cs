using System.Security.Claims;
using AIVES.BLL.Interfaces;
using AIVES.BLL.Models;
using AIVES.BLL.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StudentNameMVC.Security;
using StudentNameMVC.ViewModels;

using Microsoft.AspNetCore.RateLimiting;

namespace StudentNameMVC.Controllers;

public class AccountController : Controller
{
    private readonly IAuthService _authService;

    public AccountController(IAuthService authService)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
    }

    #region Authentication (TV2 - AUTH-01, AUTH-02, FIX-03)

    [AllowAnonymous]
    [HttpGet]
    public IActionResult ForgotPassword() => View(new ForgotPasswordViewModel());

    [AllowAnonymous]
    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("password-reset")]
    public async Task<IActionResult> ForgotPassword(
        ForgotPasswordViewModel model,
        [FromServices] IPasswordResetEmailSender resetEmailSender,
        [FromServices] ILogger<AccountController> logger,
        CancellationToken ct)
    {
        if (!ModelState.IsValid) return View(model);
        try
        {
            var token = await _authService.CreatePasswordResetTokenAsync(model.Email, ct);
            if (token is not null) await resetEmailSender.SendAsync(model.Email.Trim().ToLowerInvariant(), token, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogError("Password reset request could not be processed ({ErrorType}).", error.GetType().Name);
        }
        ModelState.Clear();
        return View(new ForgotPasswordViewModel { Submitted = true });
    }

    [AllowAnonymous]
    [HttpGet]
    public IActionResult ResetPassword(string? email, string? token)
    {
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["Cache-Control"] = "no-store";
        return View(new ResetPasswordViewModel { Email = email ?? string.Empty, Token = token ?? string.Empty });
    }

    [AllowAnonymous]
    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("password-reset")]
    public async Task<IActionResult> ResetPassword(ResetPasswordViewModel model, CancellationToken ct)
    {
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["Cache-Control"] = "no-store";
        if (!ModelState.IsValid) return View(model);
        if (!await _authService.ResetPasswordAsync(model.Email, model.Token, model.NewPassword, ct))
        {
            ModelState.AddModelError(string.Empty, "Liên kết khôi phục không hợp lệ, đã hết hạn hoặc đã được sử dụng.");
            return View(model);
        }
        TempData["SuccessMessage"] = "Đã đặt lại mật khẩu. Vui lòng đăng nhập bằng mật khẩu mới.";
        return RedirectToAction(nameof(Login));
    }

    [AllowAnonymous]
    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToDefaultRolePage();
        }

        return View(new LoginViewModel
        {
            ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null
        });
    }

    [AllowAnonymous]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(
        LoginViewModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var result = await _authService.AuthenticateAsync(
            model.Email,
            model.Password,
            cancellationToken);

        if (!result.Succeeded || result.User is null)
        {
            ModelState.AddModelError(string.Empty, "Email hoặc mật khẩu không chính xác.");
            return View(model);
        }

        var principal = AuthenticationClaimsFactory.CreatePrincipal(result.User);
        var properties = new AuthenticationProperties
        {
            IsPersistent = model.RememberMe,
            AllowRefresh = true
        };

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            properties);

        if (Url.IsLocalUrl(model.ReturnUrl) && CanAccessReturnUrl(principal, model.ReturnUrl))
        {
            return LocalRedirect(model.ReturnUrl);
        }

        return RedirectToDefaultRolePage(principal);
    }

    [Authorize]
    [HttpGet]
    public IActionResult Dashboard()
    {
        if (User.IsInRole(ApplicationRoles.Admin))
        {
            return RedirectToAction(nameof(AdminDashboard));
        }

        if (User.IsInRole(ApplicationRoles.Lecturer))
        {
            return RedirectToAction(nameof(LecturerDashboard));
        }

        return RedirectToAction(nameof(StudentDashboard));
    }

    [Authorize(Roles = ApplicationRoles.Admin)]
    [HttpGet]
    public IActionResult AdminDashboard() => View();

    [Authorize(Roles = ApplicationRoles.Lecturer)]
    [HttpGet]
    public IActionResult LecturerDashboard() => View();

    [Authorize(Roles = ApplicationRoles.Staff)]
    [HttpGet]
    public IActionResult StudentDashboard() => View();

    [Authorize]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }

    [AllowAnonymous]
    [HttpGet]
    public IActionResult AccessDenied() => View();

    private IActionResult RedirectToDefaultRolePage(ClaimsPrincipal? user = null)
    {
        user ??= User;

        if (user.IsInRole(ApplicationRoles.Admin))
        {
            return RedirectToAction(nameof(AdminDashboard));
        }

        if (user.IsInRole(ApplicationRoles.Lecturer))
        {
            return RedirectToAction(nameof(LecturerDashboard));
        }

        return RedirectToAction(nameof(StudentDashboard));
    }

    private static bool CanAccessReturnUrl(ClaimsPrincipal user, string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl) || !returnUrl.StartsWith('/'))
        {
            return false;
        }

        if (returnUrl.StartsWith("//") || returnUrl.StartsWith("/\\"))
        {
            return false;
        }

        var path = returnUrl.Split('?')[0].Split('#')[0].TrimEnd('/');
        if (string.IsNullOrEmpty(path))
        {
            path = "/";
        }

        if (path.Equals("/Account/Login", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/Account/Logout", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/Account/AccessDenied", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var isAdmin = user.IsInRole(ApplicationRoles.Admin);
        var isLecturer = user.IsInRole(ApplicationRoles.Lecturer);
        var isStaff = user.IsInRole(ApplicationRoles.Staff);

        // Admin-only paths
        if (path.StartsWith("/Admin", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/Account/Index", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/Account/AdminDashboard", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/Account/Create", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/Account/Edit", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/Account/Delete", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/Account/Details", StringComparison.OrdinalIgnoreCase))
        {
            return isAdmin;
        }

        // Lecturer-only dashboard
        if (path.Equals("/Account/LecturerDashboard", StringComparison.OrdinalIgnoreCase))
        {
            return isLecturer;
        }

        // Class reports accessible to Admin and Lecturer
        if (path.StartsWith("/ClassReports", StringComparison.OrdinalIgnoreCase))
        {
            return isAdmin || isLecturer;
        }

        // Student-only dashboard
        if (path.Equals("/Account/StudentDashboard", StringComparison.OrdinalIgnoreCase))
        {
            return isStaff;
        }

        // Student reports accessible to Admin and Staff
        if (path.StartsWith("/StudentReports", StringComparison.OrdinalIgnoreCase))
        {
            return isAdmin || isStaff;
        }

        return true;
    }

    #endregion

    #region Admin Account Management (Converted to Razor Pages in Pages/Account/Index; marked [NonAction] until Step 3 removal)

    [NonAction]
    [Authorize(Roles = ApplicationRoles.Admin)]
    [HttpGet]
    public async Task<IActionResult> Index(
        string? keyword,
        byte? roleFilter,
        [FromServices] ISystemAccountService accountService,
        CancellationToken ct = default)
    {
        if (roleFilter.HasValue && roleFilter.Value is not (1 or 2))
        {
            ModelState.AddModelError(nameof(roleFilter), "Vai trò lọc không hợp lệ. Chỉ chấp nhận Sinh viên (1) hoặc Giảng viên (2).");
            roleFilter = null;
        }

        var accounts = await accountService.SearchAccountsAsync(keyword, roleFilter, includeDeleted: true, ct);

        var items = new List<AccountItemViewModel>();
        foreach (var a in accounts)
        {
            var isReferenced = await accountService.IsAccountReferencedAsync(a.AccountId, ct);
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

        var viewModel = new AccountIndexViewModel
        {
            Keyword = keyword,
            RoleFilter = roleFilter,
            Accounts = items
        };

        return View(viewModel);
    }

    [NonAction]
    [Authorize(Roles = ApplicationRoles.Admin)]
    [HttpGet]
    public IActionResult CreateModal()
    {
        return PartialView("_CreateModalPartial", new AccountCreateViewModel());
    }

    [NonAction]
    [Authorize(Roles = ApplicationRoles.Admin)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        AccountCreateViewModel model,
        [FromServices] ISystemAccountService accountService,
        CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            Response.StatusCode = 400;
            return PartialView("_CreateModalPartial", model);
        }

        try
        {
            await accountService.CreateAccountAsync(
                model.AccountName,
                model.AccountEmail,
                model.AccountPassword,
                model.AccountRole,
                ct);

            TempData["SuccessMessage"] = "Tạo mới tài khoản thành công!";
            return Json(new { success = true, redirectUrl = Url.Action(nameof(Index)) });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            Response.StatusCode = 400;
            return PartialView("_CreateModalPartial", model);
        }
    }

    [NonAction]
    [Authorize(Roles = ApplicationRoles.Admin)]
    [HttpGet]
    public async Task<IActionResult> EditModal(
        int id,
        [FromServices] ISystemAccountService accountService,
        CancellationToken ct)
    {
        if (id <= 0) return NotFound();

        var account = await accountService.GetAccountByIdAsync(id, includeDeleted: true, ct);
        if (account == null) return NotFound();

        var model = new AccountEditViewModel
        {
            AccountId = account.AccountId,
            AccountName = account.AccountName,
            AccountEmail = account.AccountEmail,
            AccountRole = account.AccountRole
        };

        return PartialView("_EditModalPartial", model);
    }

    [NonAction]
    [Authorize(Roles = ApplicationRoles.Admin)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(
        int id,
        AccountEditViewModel model,
        [FromServices] ISystemAccountService accountService,
        CancellationToken ct)
    {
        if (id != model.AccountId) return BadRequest();

        if (!ModelState.IsValid)
        {
            Response.StatusCode = 400;
            return PartialView("_EditModalPartial", model);
        }

        try
        {
            var updated = await accountService.UpdateAccountAsync(
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
            return Json(new { success = true, redirectUrl = Url.Action(nameof(Index)) });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            Response.StatusCode = 400;
            return PartialView("_EditModalPartial", model);
        }
    }

    [NonAction]
    [Authorize(Roles = ApplicationRoles.Admin)]
    [HttpGet]
    public async Task<IActionResult> DeleteModal(
        int id,
        [FromServices] ISystemAccountService accountService,
        CancellationToken ct)
    {
        if (id <= 0) return NotFound();

        var account = await accountService.GetAccountByIdAsync(id, includeDeleted: true, ct);
        if (account == null) return NotFound();

        var model = new AccountDeleteViewModel
        {
            AccountId = account.AccountId,
            AccountName = account.AccountName,
            AccountEmail = account.AccountEmail,
            RoleName = account.AccountRole == 1 ? "Sinh viên" : "Giảng viên",
            IsHardDelete = false
        };

        return PartialView("_DeleteConfirmPartial", model);
    }

    [NonAction]
    [Authorize(Roles = ApplicationRoles.Admin)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(
        int id,
        [FromServices] ISystemAccountService accountService,
        CancellationToken ct)
    {
        if (id <= 0) return BadRequest();

        var deleted = await accountService.SoftDeleteAccountAsync(id, ct);
        if (!deleted)
        {
            return NotFound();
        }

        TempData["SuccessMessage"] = "Khóa tài khoản thành công!";
        return Json(new { success = true, redirectUrl = Url.Action(nameof(Index)) });
    }

    [NonAction]
    [Authorize(Roles = ApplicationRoles.Admin)]
    [HttpGet]
    public async Task<IActionResult> HardDeleteModal(
        int id,
        [FromServices] ISystemAccountService accountService,
        CancellationToken ct)
    {
        if (id <= 0) return NotFound();

        var account = await accountService.GetAccountByIdAsync(id, includeDeleted: true, ct);
        if (account == null) return NotFound();

        var isReferenced = await accountService.IsAccountReferencedAsync(id, ct);

        var model = new AccountDeleteViewModel
        {
            AccountId = account.AccountId,
            AccountName = account.AccountName,
            AccountEmail = account.AccountEmail,
            RoleName = account.AccountRole == 1 ? "Sinh viên" : "Giảng viên",
            IsHardDelete = true,
            IsReferenced = isReferenced
        };

        return PartialView("_HardDeleteConfirmPartial", model);
    }

    [NonAction]
    [Authorize(Roles = ApplicationRoles.Admin)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> HardDelete(
        int id,
        [FromServices] ISystemAccountService accountService,
        CancellationToken ct)
    {
        if (id <= 0) return BadRequest();

        var result = await accountService.HardDeleteAccountAsync(id, ct);
        if (result == AccountDeleteResult.InUse)
        {
            Response.StatusCode = 400;
            return Json(new
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
        return Json(new { success = true, redirectUrl = Url.Action(nameof(Index)) });
    }

    #endregion
}
