# Hướng Dẫn Thiết Lập Database & Cấu Hình Kết Nối AIVES

> Phạm vi: hướng dẫn này dành cho **schema News hiện hữu** (SystemAccount/Category/NewsArticle/Tag/NewsTag), không tạo schema thi vấn đáp nhóm 6. Không chạy lại scripts để chuẩn bị demo báo cáo thi. Định hướng hiện hành và điều kiện thay DB nằm ở [ARCHITECTURE.md](ARCHITECTURE.md) và [G6-IMPLEMENTATION-HANDOFF.md](G6-IMPLEMENTATION-HANDOFF.md). Các bước dưới chỉ áp dụng khi được giao setup/bảo trì DB News riêng.

Tài liệu này hướng dẫn cách chạy script SQL Server để khởi tạo cơ sở dữ liệu `AIVES` và cấu hình chuỗi kết nối trong `appsettings.json` cho ứng dụng ASP.NET Core MVC (PRN222 Assignment 1).

---

## 1. Giới Thiệu Các Script Database

Toàn bộ script quản lý cơ sở dữ liệu theo định hướng **DB-First** được lưu trữ trong thư mục [`database/`](../database):

1. **[`001-create-schema.sql`](../database/001-create-schema.sql):**
   - Tạo cơ sở dữ liệu `AIVES` trên SQL Server nếu chưa tồn tại.
   - Tạo các bảng cơ sở: `SystemAccount`, `Category`, `Tag`, `NewsArticle`, `NewsTag`.
   - Thiết lập các ràng buộc toàn vẹn: Primary Key, Foreign Key, Unique Index trên `AccountEmail`, Check Constraints (`CK_NewsArticle_NewsStatus`, `CK_SystemAccount_AccountRole`, v.v.).
2. **[`002-add-system-account-soft-delete.sql`](../database/002-add-system-account-soft-delete.sql):**
   - Bổ sung cột `IsDeleted BIT NOT NULL DEFAULT 0` vào bảng `SystemAccount`.
   - Cập nhật ràng buộc và filtered index hỗ trợ tính năng Soft-Delete theo quyết định nghiệp vụ `SC-10` và `SC-11`.

---

## 2. Các Bước Chạy Script Trên Microsoft SQL Server

Bạn có thể chạy script bằng một trong các công cụ sau:

### Cách 1: Sử dụng SQL Server Management Studio (SSMS) — Khuyến nghị
1. Mở **SSMS** và kết nối tới SQL Server instance của bạn (ví dụ: `localhost`, `.`, hoặc `.\SQLEXPRESS`).
2. Nhấn `Ctrl + O` (hoặc vào **File** $\rightarrow$ **Open** $\rightarrow$ **File...**).
3. Chọn và mở file `database/001-create-schema.sql`.
4. Nhấn **Execute** (hoặc phím **F5**) để chạy script tạo DB `AIVES` và các bảng.
5. Tiếp tục mở file `database/002-add-system-account-soft-delete.sql`.
6. Nhấn **Execute** (**F5**) để áp dụng cập nhật soft-delete.
7. Kiểm tra Object Explorer: Cơ sở dữ liệu `AIVES` đã xuất hiện cùng 5 bảng dữ liệu.

### Cách 2: Sử dụng dòng lệnh `sqlcmd` (Terminal / PowerShell)
Mở PowerShell tại thư mục gốc của repository và chạy:

```powershell
# Chạy script 001 khởi tạo schema
sqlcmd -S localhost -E -i database/001-create-schema.sql

# Chạy script 002 thêm cột soft-delete
sqlcmd -S localhost -E -i database/002-add-system-account-soft-delete.sql
```
*(Nếu sử dụng SQL Server Express, thay `-S localhost` bằng `-S .\SQLEXPRESS`. Nếu dùng SQL Authentication, thay `-E` bằng `-U sa -P YourPassword`).*

---

## 3. Cấu Hình Chuỗi Kết Nối Trong `appsettings.json`

Thư mục `StudentNameMVC/` đã cung cấp sẵn file mẫu [`appsettings.json.example`](../StudentNameMVC/appsettings.json.example).

### Bước 3.1: Tạo file `appsettings.json`
Copy file mẫu thành file cấu hình chính thức:
```powershell
cp StudentNameMVC/appsettings.json.example StudentNameMVC/appsettings.json
```

### Bước 3.2: Cấu hình `DefaultConnection`
Mở file `StudentNameMVC/appsettings.json` và điều chỉnh chuỗi kết nối phù hợp với máy của bạn:

```json
{
  "ConnectionStrings": {
    // Trường hợp 1: Dùng SQL Server mặc định (Windows Authentication)
    "DefaultConnection": "Server=localhost;Database=AIVES;Trusted_Connection=True;TrustServerCertificate=True;"

    // Trường hợp 2: Dùng SQL Server Express
    // "DefaultConnection": "Server=.\\SQLEXPRESS;Database=AIVES;Trusted_Connection=True;TrustServerCertificate=True;"

    // Trường hợp 3: Dùng tài khoản sa / SQL Authentication
    // "DefaultConnection": "Server=localhost,1433;Database=AIVES;User Id=sa;Password=MatKhauCuaBan;TrustServerCertificate=True;"
  },
  "DefaultAdmin": {
    "Email": "admin@AIVESSystem.org",
    "Password": "@@abc123@@"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*"
}
```

> [!IMPORTANT]
> - Tên database bắt buộc phải là **`AIVES`** để khớp với script SQL và DbContext.
> - Thông tin `DefaultAdmin` (`admin@AIVESSystem.org` / `@@abc123@@`) được ứng dụng đọc trực tiếp từ `appsettings.json` để đăng nhập quyền Administrator theo yêu cầu đề bài Assignment 1.

---

## 4. Kiểm Tra Build & Khởi Chạy Ứng Dụng

Sau khi cấu hình xong, kiểm tra build và chạy thử:

```powershell
# 1. Build toàn bộ solution
dotnet build AIVESSystem.sln

# 2. Khởi chạy ứng dụng Web MVC
dotnet run --project StudentNameMVC/StudentNameMVC.csproj
```

Mở trình duyệt truy cập đường dẫn hiển thị (ví dụ: `https://localhost:7123`). Ứng dụng sẽ tự động chuyển hướng về trang đăng nhập mặc định: `/Account/Login`.

> [!TIP]
> **Cấu hình OpenAI thật (tuỳ chọn cho Viva Interview):** Môi trường Development mặc định sử dụng mock offline (`AiServices:UseMock = true`). Để dùng OpenAI thật, cấu hình qua giao diện Admin `/Admin/AiSettings` hoặc biến môi trường `AiServices__OpenAI__ApiKey=<your-api-key>` và `AiServices__UseMock=false`.

---

## 5. Bật AI Thật Cho Demo (OpenAI)

Để trình diễn tính năng thi vấn đáp với AI thật (Whisper STT, OpenAI TTS, GPT Follow-up):

### Cách 1: Cấu hình trực tiếp trên giao diện Admin (Khuyến nghị)
1. Đăng nhập với tài khoản Administrator (`admin@AIVESSystem.org` / `@@abc123@@`).
2. Trên thanh điều hướng, chọn menu **"Cấu hình AI"** (hoặc truy cập trực tiếp `/Admin/AiSettings`).
3. Dán OpenAI API key vào ô **OpenAI API Key**.
4. Bật công tắc **"Dùng AI thật (Whisper STT, OpenAI TTS, GPT Follow-up)"**.
5. Nhấn **"Lưu cấu hình"**.
6. Nhấn nút **"Kiểm tra kết nối"** để đảm bảo key hoạt động (hệ thống sẽ gọi endpoint `/v1/models` miễn phí để kiểm tra).
7. Vào menu **"Thi vấn đáp"** để tạo phiên thi và trải nghiệm với giọng đọc và nhận diện giọng nói thật.
8. **Sau khi demo:** Nhấn **"Xoá key"** hoặc tắt công tắc "Dùng AI thật" để hệ thống quay về chế độ mô phỏng (mock).

> [!CAUTION]
> **Bảo mật API Key:**
> - Đặt giới hạn chi tiêu (usage limit) trên trang quản trị OpenAI trước khi demo.
> - Key được mã hoá bằng ASP.NET Data Protection và chỉ lưu cục bộ tại `StudentNameMVC/App_Data/ai-credentials.json` (đã được gitignore).
> - Tuyệt đối không dán key vào tin nhắn, issue, PR hoặc commit vào repository.
> - Nếu nghi ngờ lộ key, hãy thu hồi (Revoke) ngay lập tức trên bảng điều khiển OpenAI.

### Cách 2: Sử dụng biến môi trường (Cho máy chủ / CI)
Bạn cũng có thể cấu hình thông qua biến môi trường hoặc .NET User Secrets:
```powershell
$env:AiServices__OpenAI__ApiKey="sk-..."
$env:AiServices__UseMock="false"
```
*(Lưu ý: Cấu hình lưu qua giao diện Admin sẽ được ưu tiên hơn so với biến môi trường).*
