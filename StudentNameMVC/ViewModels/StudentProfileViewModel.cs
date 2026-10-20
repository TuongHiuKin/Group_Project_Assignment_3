using System.ComponentModel.DataAnnotations;

namespace StudentNameMVC.ViewModels;

public class StudentProfileViewModel
{
    public int AccountId { get; set; }

    [Display(Name = "Mã số sinh viên (MSSV)")]
    public string StudentId { get; set; } = "SV01";

    [Required(ErrorMessage = "Vui lòng nhập họ và tên")]
    [StringLength(100, MinimumLength = 2, ErrorMessage = "Họ và tên từ 2 đến 100 ký tự")]
    [Display(Name = "Họ và tên sinh viên")]
    public string StudentName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập địa chỉ email")]
    [EmailAddress(ErrorMessage = "Định dạng email không hợp lệ")]
    [Display(Name = "Địa chỉ Email")]
    public string Email { get; set; } = string.Empty;

    [Display(Name = "Số điện thoại liên hệ")]
    [RegularExpression(@"^0\d{9,10}$", ErrorMessage = "Số điện thoại phải bắt đầu bằng 0 và gồm 10-11 số")]
    public string? Phone { get; set; } = "0912345678";

    [Required(ErrorMessage = "Vui lòng chọn lớp học phần")]
    [Display(Name = "Lớp học phần (GV phân công)")]
    public string ClassId { get; set; } = "SE1701";

    [Display(Name = "Chuyên ngành đào tạo")]
    public string Major { get; set; } = "Kỹ thuật phần mềm (SE)";

    [Display(Name = "Giảng viên phụ trách lớp")]
    public string LecturerName { get; set; } = "TS. Hoàng Yến (GV01)";

    [Display(Name = "Khóa đào tạo")]
    public string AcademicYear { get; set; } = "K17 (2023 - 2027)";

    [Display(Name = "Học kỳ hiện tại")]
    public string Semester { get; set; } = "Fall 2026";

    [Display(Name = "Vai trò hệ thống")]
    public string RoleName { get; set; } = "Sinh viên";

    [StringLength(100, MinimumLength = 6, ErrorMessage = "Mật khẩu mới phải từ 6 ký tự trở lên")]
    [DataType(DataType.Password)]
    [Display(Name = "Mật khẩu mới (Để trống nếu không muốn đổi)")]
    public string? NewPassword { get; set; }

    [DataType(DataType.Password)]
    [Compare("NewPassword", ErrorMessage = "Mật khẩu xác nhận không khớp")]
    [Display(Name = "Xác nhận mật khẩu mới")]
    public string? ConfirmPassword { get; set; }
}
