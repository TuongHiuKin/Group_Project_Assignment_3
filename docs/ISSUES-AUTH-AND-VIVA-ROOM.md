# Báo cáo Sự cố & Issue Backlog (Test Feedback - 20/10/2026)

Tài liệu này tổng hợp 3 lỗi nghiệp vụ và điều hướng phát hiện trong quá trình kiểm thử thực tế trên hệ thống **AIVES**, dành cho Tech Lead và các thành viên phụ trách (Frontend, Auth/BLL) nắm tình hình và lên kế hoạch xử lý.

---

## Danh sách Sự cố

| Mã Issue | Tiêu đề | Phân hệ | Mức độ | Người phụ trách dự kiến |
| :--- | :--- | :--- | :--- | :--- |
| **ISSUE-BUG-01** | Admin đăng nhập thành công bị chuyển hướng sai vào `StudentDashboard` | Auth / AccountController | **High** | Tech Lead / Member 1 |
| **ISSUE-BUG-02** | Giảng viên đăng nhập bị lỗi `AccessDenied` (`/Account/Index` & `StudentDashboard`) | Auth / Routing / RBAC | **High** | Tech Lead / Member 1 |
| **ISSUE-BUG-03** | Không thể rời phòng thi Viva từ nút "Rời phòng" trên Header | Razor Pages / VivaLayout | **High** | Frontend / Member 2 |

---

## Chi tiết từng Issue

### ISSUE-BUG-01: Admin đăng nhập thành công bị chuyển hướng sai vào `StudentDashboard`

#### 1. Mô tả triệu chứng (Symptoms)
- Khi sử dụng tài khoản Admin mặc định (`admin@AIVESSystem.org` / `@@abc123@@`) đăng nhập tại trang `/Account/Login`, sau khi đăng nhập thành công, hệ thống chuyển hướng Admin tới URL:
  `/Account/StudentDashboard` (hoặc hiển thị giao diện của Sinh viên) thay vì `/Account/AdminDashboard`.

#### 2. Phân tích nguyên nhân kỹ thuật (Root Cause)
- Tại file `StudentNameMVC/Controllers/AccountController.cs`:
  - Trong action `Login` (HTTP POST):
    ```csharp
    await HttpContext.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        principal,
        properties);

    if (Url.IsLocalUrl(model.ReturnUrl))
    {
        return LocalRedirect(model.ReturnUrl);
    }

    return RedirectToDefaultRolePage();
    ```
  - Trong phương thức `RedirectToDefaultRolePage()`:
    ```csharp
    private IActionResult RedirectToDefaultRolePage()
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
    ```
- **Lý do**:
  - `User` thuộc tính của `ControllerBase` đại diện cho danh tính (`ClaimsPrincipal`) của **request gửi lên hiện tại** (được Cookie Middleware giải mã trước khi vào Action).
  - Lệnh `HttpContext.SignInAsync` chỉ phát hành header `Set-Cookie` cho response tiếp theo của trình duyệt, **không cập nhật thuộc tính `User` hay `HttpContext.User` trong luồng request POST hiện tại**.
  - Vì vậy, `User.Identity.IsAuthenticated` tại thời điểm gọi `RedirectToDefaultRolePage()` vẫn là `false`.
  - Cả hai điều kiện `User.IsInRole(ApplicationRoles.Admin)` và `User.IsInRole(ApplicationRoles.Lecturer)` đều trả về `false`.
  - Luồng xử lý luôn rơi vào nhánh fallback mặc định cuối cùng: `return RedirectToAction(nameof(StudentDashboard));`.

#### 3. Tác động (Impact)
- Admin bị đưa vào sai trang giao diện (Student Dashboard), gây trải nghiệm sai lệch và vi phạm phân tách vai trò.

#### 4. Đề xuất phương án sửa (Proposed Solution)
- Cập nhật hàm `RedirectToDefaultRolePage` nhận tham số role hoặc `ClaimsPrincipal` đã được giải mã từ `result.User`:
  ```csharp
  private IActionResult RedirectToDefaultRolePage(string? role = null)
  {
      role ??= User.FindFirstValue(ClaimTypes.Role);
      if (string.Equals(role, ApplicationRoles.Admin, StringComparison.OrdinalIgnoreCase))
      {
          return RedirectToAction(nameof(AdminDashboard));
      }
      if (string.Equals(role, ApplicationRoles.Lecturer, StringComparison.OrdinalIgnoreCase))
      {
          return RedirectToAction(nameof(LecturerDashboard));
      }
      return RedirectToAction(nameof(StudentDashboard));
  }
  ```
- Tại action `Login`: gọi `return RedirectToDefaultRolePage(result.User.Role);`.

---

### ISSUE-BUG-02: Giảng viên đăng nhập bị lỗi `AccessDenied` (`/Account/Index` & `StudentDashboard`)

#### 1. Mô tả triệu chứng (Symptoms)
- Khi Giảng viên đăng nhập, hệ thống điều hướng người dùng tới trang từ chối quyền truy cập:
  `https://localhost:7062/Account/AccessDenied?ReturnUrl=%2FAccount%2FIndex`
- Hoặc nếu không có ReturnUrl, Giảng viên bị rơi vào `AccessDenied` với `ReturnUrl=%2FAccount%2FStudentDashboard`.

#### 2. Phân tích nguyên nhân kỹ thuật (Root Cause)
Sự cố xảy ra từ hai nguyên nhân kết hợp:
1. **Trường hợp có `ReturnUrl = /Account/Index`**:
   - Khi truy cập vào đường dẫn gốc hoặc gõ `/Account` trên trình duyệt khi chưa đăng nhập, cơ chế định tuyến mặc định của MVC nhận diện action mặc định là `Index` -> URL yêu cầu là `/Account/Index`.
   - Action `AccountController.Index` được bảo vệ nghiêm ngặt bằng:
     ```csharp
     [Authorize(Roles = ApplicationRoles.Admin)]
     [HttpGet]
     public async Task<IActionResult> Index(...)
     ```
   - Hệ thống tự động redirect sang `/Account/Login?ReturnUrl=%2FAccount%2FIndex`.
   - Giảng viên nhập thông tin đăng nhập thành công. Action `Login` kiểm tra:
     ```csharp
     if (Url.IsLocalUrl(model.ReturnUrl))
     {
         return LocalRedirect(model.ReturnUrl);
     }
     ```
   - Giảng viên được chuyển hướng về `/Account/Index`. Nhưng vì Giảng viên mang role `Lecturer` (không phải `Admin`), ASP.NET Core Authorization Middleware chặn quyền và chuyển hướng tới:
     `/Account/AccessDenied?ReturnUrl=%2FAccount%2FIndex`.
2. **Trường hợp không có ReturnUrl**:
   - Do bug điều hướng ở **ISSUE-BUG-01**, hệ thống tự động redirect Giảng viên sang `/Account/StudentDashboard`.
   - Action `StudentDashboard` chỉ cho phép `[Authorize(Roles = ApplicationRoles.Staff)]`.
   - Giảng viên không có role `Staff`, do đó cũng bị chặn quyền và đẩy về:
     `/Account/AccessDenied?ReturnUrl=%2FAccount%2FStudentDashboard`.

#### 3. Tác động (Impact)
- Giảng viên hoàn toàn không thể vào được hệ thống hoặc dashboard của mình (`LecturerDashboard`), bị chặn ở trang AccessDenied ngay sau khi đăng nhập.

#### 4. Đề xuất phương án sửa (Proposed Solution)
- Khắc phục **ISSUE-BUG-01** để khi không có ReturnUrl, Giảng viên sẽ được chuyển hướng thẳng tới `LecturerDashboard`.
- Trong action `Login`, trước khi thực hiện `LocalRedirect(model.ReturnUrl)`:
  Kiểm tra xem ReturnUrl có yêu cầu quyền Admin hay không (ví dụ `/Account/Index` hoặc `/Admin/*`). Nếu user đăng nhập không phải Admin thì bỏ qua `ReturnUrl` này và đưa về trang mặc định theo role của họ (`RedirectToDefaultRolePage(result.User.Role)`).

---

### ISSUE-BUG-03: Không thể rời phòng thi Viva từ nút "Rời phòng" trên Header

#### 1. Mô tả triệu chứng (Symptoms)
- Trong phòng thi vấn đáp Viva (`/Viva/Exam/{sessionId}`):
- Trên thanh Header góc trên bên phải có nút: `[Rời phòng]`.
- Khi người dùng (Sinh viên/Thí sinh) nhấp vào nút này, hộp thoại xác nhận hiện lên: *"Bạn có chắc chắn muốn rời phòng thi không?"*.
- Người dùng bấm "OK" (xác nhận), nhưng giao diện phòng thi vẫn đứng yên, không có phản hồi điều hướng và không thoát ra Dashboard được.

#### 2. Phân tích nguyên nhân kỹ thuật (Root Cause)
- Mã nguồn tại `StudentNameMVC/Pages/Shared/_VivaLayout.cshtml` (dòng 80-83):
  ```html
  <a asp-controller="Account" asp-action="Dashboard" class="btn btn-outline-light btn-sm rounded-pill px-3"
     onclick="return confirm('Bạn có chắc chắn muốn rời phòng thi không?');">
      <i class="bi bi-box-arrow-left me-1"></i> Rời phòng
  </a>
  ```
- **Lý do TagHelper không được kích hoạt**:
  - Trong cấu trúc thư mục Razor Pages, tệp `_ViewImports.cshtml` chỉ tồn tại bên trong các thư mục con:
    - `StudentNameMVC/Pages/Viva/_ViewImports.cshtml`
    - `StudentNameMVC/Pages/Admin/_ViewImports.cshtml`
  - Thư mục layout dùng chung `StudentNameMVC/Pages/Shared/` cũng như thư mục gốc `StudentNameMVC/Pages/` **không có file `_ViewImports.cshtml`**.
  - Tệp `_VivaLayout.cshtml` cũng không có dòng `@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers`.
  - Kết quả biên dịch: Trình duyệt nhận được mã HTML thô:
    ```html
    <a asp-controller="Account" asp-action="Dashboard" class="btn btn-outline-light btn-sm rounded-pill px-3" onclick="return confirm('Bạn có chắc chắn muốn rời phòng thi không?');">
        <i class="bi bi-box-arrow-left me-1"></i> Rời phòng
    </a>
    ```
  - Thẻ `<a>` này **hoàn toàn không có thuộc tính `href`**!
  - Khi người dùng click, sự kiện JavaScript `confirm` chạy trả về `true`, nhưng do thẻ `<a>` không có `href`, trình duyệt không điều hướng đi bất kỳ đâu.

#### 3. Tác động (Impact)
- Thí sinh/sinh viên bị kẹt lại trong phòng thi khi muốn kết thúc hoặc thoát ra màn hình chính, gây hiểu lầm là hệ thống bị treo hoặc mất kết nối.

#### 4. Đề xuất phương án sửa (Proposed Solution)
1. Tạo tệp `StudentNameMVC/Pages/_ViewImports.cshtml` ở thư mục gốc của Pages để toàn bộ Razor Pages và Shared Layouts đều tự động thừa kế `Microsoft.AspNetCore.Mvc.TagHelpers`.
2. Hoặc cập nhật trực tiếp tại `StudentNameMVC/Pages/Shared/_VivaLayout.cshtml`:
   - Thêm `@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers` ở đầu file.
   - Hoặc gán trực tiếp URL chuẩn: `href="/Account/Dashboard"`.
