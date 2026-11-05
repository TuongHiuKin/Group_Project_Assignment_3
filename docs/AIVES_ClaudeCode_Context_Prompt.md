# PROMPT NẠP NGỮ CẢNH DỰ ÁN AIVES CHO CLAUDE CODE

Cách dùng: mở Claude Code trong folder `D:\School\AIVES_AI_powered_Viva_Exam_System`, dán toàn bộ nội dung bên dưới làm tin nhắn đầu tiên. Muốn không phải dán lại mỗi lần thì lưu nội dung này thành `CLAUDE.md` ở gốc folder (và thêm `CLAUDE.md` vào `.gitignore` nếu không muốn đẩy lên git).

---

Bạn đang làm việc trong dự án AIVES (AI-powered Viva Examination System), repo GitHub `TuongHiuKin/PRN_Assignment_2`, cho Assignment 2 môn PRN. Đọc kỹ ngữ cảnh dưới đây, rồi **chưa sửa gì** cho đến khi hoàn thành mục "Việc đầu tiên".

## 1. Kiến trúc
- .NET 8, mô hình 3 lớp. Giải pháp có các project: `StudentNameMVC` (Web, thực chất là Razor Pages), `AIVES.BLL`, `AIVES.DAL` (EF Core, database-first, script SQL 001 đến 004), `AIVES.Tests`.
- **Presentation (`StudentNameMVC`):** toàn bộ giao diện là Razor Pages dưới `Pages/` (`Viva/Index`, `Viva/Exam`, `Account/*`, `Admin/AiSettings`, `ClassReports/*`, `StudentReports/*`, `Report/*`, `NewsArticle/Index`). Controllers và Views MVC cũ đã được gỡ ở PR #70. SignalR Hub ở `/hubs/viva` (`VivaHub`, `[Authorize]`, mỗi phiên một group), cùng `VivaTimerWorker` tick mỗi giây.
- **Business (`AIVES.BLL`):** `VivaSessionOrchestrator`, các adapter AI (`OpenAiProviderAdapter`, `OpenAiCompatibleProviderAdapter`, `GeminiProviderAdapter`), `AiProviderRegistry`, `ProtectedAiCredentialStore` (mã hóa bằng Data Protection), các mock service, `ClassReportService`, `StudentReportService`, và `ExamReportRepository` (nằm trong BLL, đọc dữ liệu thật qua các repository của DAL).
- **Data (`AIVES.DAL`):** repository và `DbContext`, SQL Server, DB tên AIVES.
- Xác thực cookie `AIVES.Auth`, vai trò Admin, Lecturer, Staff (Staff hiển thị là "Sinh viên").
- BLL không biết SignalR: dùng interface `IVivaRealtimePublisher`, bản cài đặt nằm ở Web.

## 2. Luồng thi vấn đáp (Viva)
1. Sinh viên nhập mã đề `EXAM01` ở `/Viva/Index` và tạo phiên (`VivaSession`).
2. Vào `/Viva/Exam/{sessionId}`, bấm "Bắt đầu làm bài thi": AI đọc câu hỏi (TTS) và đồng hồ đếm ngược chạy.
3. **Client chỉ gọi `BeginAnswering` sau khi audio câu hỏi đọc xong.** Nộp audio trước thời điểm đó sẽ bị lỗi 400 "The answering period is not active or has expired".
4. Sinh viên ghi âm bằng micro (trình duyệt gửi `audio/webm;codecs=opus`) rồi nộp qua HTTP POST (`SubmitAudio`), không qua SignalR.
5. STT chuyển giọng nói thành chữ, LLM sinh câu hỏi phụ, rồi SignalR đẩy sự kiện về trình duyệt: `SessionStatusChanged`, `TranscriptReady`, `FollowUpGenerated`, `TimeRemainingChanged`, `SessionCompleted`, `VivaError`.

## 3. Cấu hình AI (Gemini)
- Cấu hình nằm trong `StudentNameMVC/App_Data/ai-credentials.json`, **bị mã hóa và chứa API key. Không mở, không đọc, không in file này.** Chỉ xem và sửa cấu hình qua trang `/Admin/AiSettings` (mục "Định tuyến năng lực").
- Tên model hợp lệ do Gemini trả về cho key của Kien (xác nhận qua nút "Lấy danh sách model", 50 model):
  - Đọc câu hỏi (TTS): `gemini-3.8-flash-tts` (đã chạy thật, khoảng 5,5 giây, ra WAV 24 kHz). Không dùng `gemini-3.8-flash-preview-tts` (lỗi 404) và `gemini-2.0-flash` không phải model TTS.
  - Nhận diện giọng nói (STT) và câu hỏi phụ (LLM): `gemini-3.8-flash`. `gemini-1.5-flash` **không có** trong danh sách, rất có thể lỗi 404.
- Lưu một hồ sơ trên `/Admin/AiSettings` có thể đặt lại routing về model mặc định của adapter. Sau khi lưu hồ sơ phải kiểm tra lại routing.
- Chế độ AI thật: `AiServices:UseMock=false`, `AllowMockFallback=false` (đặt trong `appsettings.Development.json`). Kiểm tra: tầng chọn service trong `AIVES.BLL/DependencyInjection.cs` vẫn có thể âm thầm rơi về mock khi routing thiếu hoặc hồ sơ thiếu năng lực. Khi test, luôn xác nhận file âm thanh không phải `/audio/mock-question.mp3` và transcript không phải câu mẫu "Scoped DbContext is instantiated once per HTTP request...".
- Gemini 2.5 trở lên có thể trả 503 khi quá tải, và các lệnh gọi có timeout cứng (STT 15 s, TTS 10 s, LLM 15 s trong `GeminiProviderAdapter`). Nghi vấn âm thanh `audio/webm` bị Gemini từ chối (Gemini chỉ liệt kê WAV, MP3, AIFF, AAC, OGG, FLAC) vẫn **chưa được kiểm chứng**.

## 4. Trạng thái code (quan trọng)
- Nhánh `main` trên GitHub đã gồm PR #56 đến #70 (nhiều nhà cung cấp AI, bỏ mock repository, chuyển sang Razor Pages, bỏ MVC, dashboard dùng số liệu thật).
- Máy Kien còn **các thay đổi chưa commit trong working tree** (khoảng 16 file), gồm: sắp xếp lượt thi mới nhất lên đầu (`ClassReportService`, `StudentReportService`), sửa tên model Gemini, giọng đọc dự phòng Web Speech trong `viva-signalr-client.js`, bỏ "Phòng thi Viva" khỏi sidebar Giảng viên và chặn Giảng viên tạo phiên thi, khối Rubric trên `LecturerDashboard`, và cơ chế gán điểm trong `ExamReportRepository`.
- **Cảnh báo:** điểm "8.5 / 10 - ĐẠT CHUẨN", phần nhận xét AI và 3 huy hiệu Rubric đang được gán cố định khi bài thi hoàn thành, không phụ thuộc nội dung trả lời. Đó là dữ liệu giả, trái với quyết định bỏ mock data (#57). Nếu không thay đổi thì phải có nhãn "demo". Hướng đúng là tính điểm theo luật từ transcript (ví dụ độ khớp từ khóa Rubric) hoặc để "Chưa chấm".
- Nhóm 4 (chấm điểm tự động bằng AI nâng cao) nằm ngoài phạm vi ASM2.
- Việc còn mở: #54 (triển khai qua Cloudflare tunnel để máy khác truy cập), phần tách đăng ký DAL ra khỏi Web, các lỗi UI đã nêu ở trên.

## 5. Quy tắc bắt buộc
- **Không đọc, in, ghi log hay đặt API key.** Không mở `App_Data/ai-credentials.json`. Nếu thông báo lỗi có chuỗi giống key thì thay bằng `[REDACTED]`.
- **Không commit, không push, không tạo PR hay issue** nếu Kien chưa yêu cầu. Không `git reset --hard`, `git clean -fd`, `git checkout -- .`, force-push.
- Không ghi vào DB AIVES thật ngoài những gì Kien cho phép. Không gọi AI thật quá mức tối thiểu (hạn mức tốn tiền).
- Không thêm dữ liệu giả vào giao diện. Nếu chưa có dữ liệu thật thì hiện "Chưa có dữ liệu" hoặc "Chưa chấm".
- Trả lời bằng tiếng Việt, giữ nguyên tên mã (class, file, biến) bằng tiếng Anh. Báo cáo ngắn, nêu rõ cái gì đã kiểm chứng và cái gì là suy đoán.
- Chỉ dùng tài khoản test do Kien cung cấp, trên localhost, và không ghi mật khẩu vào log hay báo cáo.
- Thư mục `.claude/`, `node_modules/`, ảnh chụp và báo cáo test không được đưa vào git.

## 6. Việc đầu tiên (chỉ đọc)
1. Chạy `git status --short` và `git diff --stat`, liệt kê các file đã sửa và tóm tắt từng thay đổi trong một câu.
2. Đối chiếu các tên model trong mục 3 với trang `/Admin/AiSettings` (chỉ đọc, không đổi) và báo lại routing STT, TTS, LLM đang trỏ tới model nào.
3. Tìm trong `ExamReportRepository` và `LecturerDashboard` chỗ điểm và Rubric được gán cố định, và cho biết có phụ thuộc vào transcript hay không.
4. Báo lại bằng một bảng ngắn: việc nào đã ổn, việc nào cần sửa, việc nào chưa rõ. Sau đó chờ Kien chọn việc, không tự sửa.
