using System.ComponentModel.DataAnnotations;
using AIVES.BLL.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace StudentNameMVC.Pages.Account;

/// <summary>
/// Sinh viên tự tạo tài khoản. Chỉ tạo được role Staff — giảng viên vẫn do Admin tạo ở
/// /Account/Index. Tài khoản mới được gán về giảng viên mặc định (cấu hình
/// <c>Registration:DefaultLecturerEmail</c>, nếu trống thì lấy giảng viên đầu tiên đang hoạt động)
/// để /ClassReports lọc được danh sách sinh viên của giảng viên đó.
/// </summary>
[AllowAnonymous]
[EnableRateLimiting("student-register")]
public class RegisterModel : PageModel
{
    private const byte LecturerRole = 2;

    private readonly ISystemAccountService _accountService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RegisterModel> _logger;

    public RegisterModel(
        ISystemAccountService accountService,
        IConfiguration configuration,
        ILogger<RegisterModel> logger)
    {
        _accountService = accountService ?? throw new ArgumentNullException(nameof(accountService));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [BindProperty]
    [Required(ErrorMessage = "Vui lòng nhập họ và tên.")]
    [StringLength(100, ErrorMessage = "Họ và tên không được vượt quá 100 ký tự.")]
    public string FullName { get; set; } = string.Empty;

    [BindProperty]
    [Required(ErrorMessage = "Vui lòng nhập email.")]
    [EmailAddress(ErrorMessage = "Email không hợp lệ.")]
    [StringLength(254, ErrorMessage = "Email không được vượt quá 254 ký tự.")]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    [Required(ErrorMessage = "Vui lòng nhập mật khẩu.")]
    [StringLength(100, MinimumLength = 6, ErrorMessage = "Mật khẩu phải có ít nhất 6 ký tự.")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    [Required(ErrorMessage = "Vui lòng nhập lại mật khẩu.")]
    [DataType(DataType.Password)]
    [Compare(nameof(Password), ErrorMessage = "Mật khẩu nhập lại không khớp.")]
    public string ConfirmPassword { get; set; } = string.Empty;

    /// <summary>Tên giảng viên phụ trách, chỉ để hiển thị cho sinh viên biết mình thuộc ai.</summary>
    public string? LecturerName { get; private set; }

    /// <summary>Đăng ký thành công thì trang chuyển sang trạng thái "đi tới đăng nhập".</summary>
    public bool Registered { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct = default)
    {
        await ResolveDefaultLecturerAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct = default)
    {
        if (!ModelState.IsValid)
        {
            await ResolveDefaultLecturerAsync(ct);
            return Page();
        }

        var defaultLecturerId = await ResolveDefaultLecturerAsync(ct);

        try
        {
            await _accountService.RegisterStudentAsync(
                FullName,
                Email,
                Password,
                defaultLecturerId,
                ct);

            Registered = true;
            ModelState.Clear();
            _logger.LogInformation("Sinh viên tự đăng ký tài khoản thành công.");
            return Page();
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            // Thông điệp từ BLL đã là tiếng Việt và an toàn để hiển thị (trùng email, độ dài…).
            ModelState.AddModelError(string.Empty, error.Message);
            await ResolveDefaultLecturerAsync(ct);
            return Page();
        }
    }

    /// <summary>
    /// Xác định giảng viên phụ trách: ưu tiên email cấu hình, nếu không có thì lấy giảng viên
    /// đầu tiên đang hoạt động theo AccountId. Trả về null khi hệ thống chưa có giảng viên nào —
    /// khi đó vẫn cho đăng ký nhưng tài khoản không gắn giảng viên.
    /// </summary>
    private async Task<int?> ResolveDefaultLecturerAsync(CancellationToken ct)
    {
        var configuredEmail = _configuration["Registration:DefaultLecturerEmail"];

        var lecturers = await _accountService.SearchAccountsAsync(
            keyword: null,
            role: LecturerRole,
            includeDeleted: false,
            ct: ct);

        if (lecturers.Count == 0)
        {
            LecturerName = null;
            return null;
        }

        var chosen = !string.IsNullOrWhiteSpace(configuredEmail)
            ? lecturers.FirstOrDefault(l => string.Equals(l.AccountEmail, configuredEmail.Trim(), StringComparison.OrdinalIgnoreCase))
            : null;

        chosen ??= lecturers.OrderBy(l => l.AccountId).First();
        LecturerName = chosen.AccountName;
        return chosen.AccountId;
    }
}
