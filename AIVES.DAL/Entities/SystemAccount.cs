namespace AIVES.DAL.Entities;

public class SystemAccount
{
    public int AccountId { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public string AccountEmail { get; set; } = string.Empty;
    public string AccountPasswordHash { get; set; } = string.Empty;
    // Database roles: 1 = Staff (sinh viên), 2 = Lecturer. Admin is configuration-only.
    public byte AccountRole { get; set; }
    public bool IsDeleted { get; set; }

    /// <summary>
    /// Giảng viên phụ trách của sinh viên. NULL với giảng viên, tài khoản Admin và các
    /// tài khoản tạo trước script 008. Sinh viên tự đăng ký được gán về giảng viên mặc định
    /// để cổng báo cáo lọc được danh sách sinh viên của từng giảng viên.
    /// </summary>
    public int? LecturerId { get; set; }

    /// <summary>Navigation tới giảng viên phụ trách (chỉ có nghĩa khi <see cref="LecturerId"/> khác NULL).</summary>
    public SystemAccount? Lecturer { get; set; }

    /// <summary>Các sinh viên do tài khoản này phụ trách (rỗng với tài khoản không phải giảng viên).</summary>
    public ICollection<SystemAccount> Students { get; set; } = new List<SystemAccount>();
}
