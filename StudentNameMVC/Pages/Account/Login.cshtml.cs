using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using AIVES.BLL.Interfaces;
using AIVES.BLL.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using StudentNameMVC.Security;

namespace StudentNameMVC.Pages.Account;

[AllowAnonymous]
public class LoginModel : PageModel
{
    private readonly IAuthService _authService;

    public LoginModel(IAuthService authService)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
    }

    [BindProperty]
    [Required(ErrorMessage = "Vui lòng nhập email.")]
    [EmailAddress(ErrorMessage = "Email không đúng định dạng.")]
    [StringLength(254, ErrorMessage = "Email không được vượt quá 254 ký tự.")]
    [Display(Name = "Email")]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    [Required(ErrorMessage = "Vui lòng nhập mật khẩu.")]
    [DataType(DataType.Password)]
    [StringLength(256, ErrorMessage = "Mật khẩu không được vượt quá 256 ký tự.")]
    [Display(Name = "Mật khẩu")]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    [Display(Name = "Duy trì đăng nhập")]
    public bool RememberMe { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public IActionResult OnGet(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToDefaultRolePage(User);
        }

        ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await _authService.AuthenticateAsync(
            Email,
            Password,
            cancellationToken);

        if (!result.Succeeded || result.User is null)
        {
            ModelState.AddModelError(string.Empty, "Email hoặc mật khẩu không chính xác.");
            return Page();
        }

        var principal = AuthenticationClaimsFactory.CreatePrincipal(result.User);
        var properties = new AuthenticationProperties
        {
            IsPersistent = RememberMe,
            AllowRefresh = true
        };

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            properties);

        if (Url.IsLocalUrl(ReturnUrl) && CanAccessReturnUrl(principal, ReturnUrl))
        {
            return LocalRedirect(ReturnUrl);
        }

        return RedirectToDefaultRolePage(principal);
    }

    public IActionResult RedirectToDefaultRolePage(ClaimsPrincipal? user = null)
    {
        user ??= User;

        if (user.IsInRole(ApplicationRoles.Admin))
        {
            return RedirectToPage("/Account/AdminDashboard");
        }

        if (user.IsInRole(ApplicationRoles.Lecturer))
        {
            return RedirectToPage("/Account/LecturerDashboard");
        }

        return RedirectToPage("/Account/StudentDashboard");
    }

    public static bool CanAccessReturnUrl(ClaimsPrincipal user, string? returnUrl)
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

        if (path.Equals("/Account/LecturerDashboard", StringComparison.OrdinalIgnoreCase))
        {
            return isLecturer;
        }

        if (path.StartsWith("/ClassReports", StringComparison.OrdinalIgnoreCase))
        {
            return isAdmin || isLecturer;
        }

        if (path.Equals("/Account/StudentDashboard", StringComparison.OrdinalIgnoreCase))
        {
            return isStaff;
        }

        if (path.StartsWith("/StudentReports", StringComparison.OrdinalIgnoreCase))
        {
            return isAdmin || isStaff;
        }

        return true;
    }
}
