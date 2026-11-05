# KỊCH BẢN KIỂM THỬ HỆ THỐNG KHẢO THÍ VẤN ĐÁP AIVES (dành cho Claude Code)

Cách dùng: mở Claude Code trong folder dự án AIVES trên máy Kien, dán nguyên file này (hoặc nói "chạy kịch bản trong file này"). Claude Code chạy từng kịch bản bằng Playwright, ghi PASS / FAIL / CHƯA CÓ TÍNH NĂNG cho từng kết quả mong đợi, rồi viết báo cáo. Claude Code **chỉ kiểm thử, không sửa code, không commit**.

## 0. Quy tắc bắt buộc
- Không đọc, in hay ghi log API key. Không mở `StudentNameMVC/App_Data/ai-credentials.json`. Nếu log hay thông báo lỗi có chuỗi giống key thì thay bằng `[REDACTED]` trước khi báo.
- Chỉ gọi AI thật ở mức tối thiểu: một phiên thi EXAM01, mỗi câu một lượt ghi âm ngắn. Không lặp vòng lại để "thử cho chắc".
- Không sửa code, cấu hình hay routing AI. Chỉ đọc. Nếu một bước bắt buộc phải đổi cấu hình thì dừng lại, ghi vào báo cáo và hỏi Kien.
- Không ghi DB ngoài các phiên thi tạo khi chạy kịch bản. Không xóa dữ liệu.
- Không `git reset --hard`, `git clean -fd`, `git checkout -- .`, không commit, không push. Không đưa thư mục `.claude/`, ảnh chụp hay báo cáo vào git.
- Mỗi kết quả mong đợi phải có bằng chứng (ảnh chụp hoặc đoạn văn bản trên trang). Không suy đoán là PASS.

## 1. Chuẩn bị
1. Chạy app trên cổng riêng để đọc log: `dotnet run -c Release --project StudentNameMVC --urls http://localhost:5071` (không tắt bản :5070 của Kien). Lấy log app vào một file trong thư mục tạm.
2. Dùng Playwright (skill `playwright-skill` đã cài trong `.claude/skills/playwright-skill`, hoặc gói `playwright` chính thức) với Chromium và cờ micro giả: `--use-fake-ui-for-media-stream --use-fake-device-for-media-stream --use-file-for-fake-audio-capture=<file.wav>`.
3. File WAV cho micro giả: dùng file TTS mà chính app vừa tạo cho câu hỏi (ở `wwwroot/audio/cache/tts-*.wav`) làm "giọng nói thí sinh". Trước đó ghi lại nội dung câu hỏi gốc để so sánh. Nếu app đang ở chế độ mock và không có file TTS thật thì ghi rõ vào báo cáo và dùng một file WAV tiếng Việt do Kien cung cấp.
4. Kiểm tra trước: trang `/Admin/AiSettings` (đăng nhập admin) cho biết AI thật bật hay tắt và routing STT/TTS/LLM trỏ tới hồ sơ nào. Chỉ ghi lại, không đổi. Nếu tên model TTS không tồn tại, TTS sẽ lỗi 404 và kịch bản 2 và 3 không thể PASS; ghi rõ điều đó.

## 2. Tài khoản kiểm thử
| Vai trò | Email | Mật khẩu | Mục đích |
|---|---|---|---|
| Giảng viên (Lecturer) | lecturer@fpt.edu.vn | @123 | Dashboard, tiêu chí Rubric, chấm điểm và báo cáo lớp |
| Sinh viên (Student/Staff) | student@fpt.edu.vn (hoặc tài khoản MSSV 1) | @123 | Vào phòng thi vấn đáp, kiểm tra TTS và STT |

Nếu tài khoản không tồn tại hoặc đăng nhập thất bại thì ghi vào báo cáo và dừng kịch bản đó, không tự tạo tài khoản mới.

## 3. Các kịch bản

### Kịch bản 1: Phân quyền Giảng viên và xem Rubric
**Mục tiêu:** Giảng viên không bị nhầm vào thi như sinh viên, xem được bộ tiêu chí Rubric của đề EXAM01.
**Các bước:** (1) Đăng nhập Giảng viên. (2) Quan sát sidebar trái. (3) Mở `/Account/LecturerDashboard`. (4) Gõ trực tiếp `/Viva` và `/Viva/Index`, rồi bấm "Tạo phiên thi mới".
**Mong đợi:**
- 1.1 Sidebar không có "Phòng thi Viva", có "Dashboard Giảng viên", "Thống kê Lớp học" và "Đánh giá & Chấm thi".
- 1.2 LecturerDashboard có khối "Tiêu chí Chấm điểm Vấn đáp AI theo Chuẩn Rubric" với đủ ý cho EXAM01-Q01, Q02, Q03.
- 1.3 `/Viva/Index` hiện thông báo màu xanh: "Tài khoản Giảng viên Khảo thí... phòng thi vấn đáp dành cho Thí sinh".
- 1.4 Bấm tạo phiên thi thì hiện lỗi chặn, không tạo phiên (kiểm tra thêm: không có phiên mới nào trong bảng VivaSession).

### Kịch bản 2: Sinh viên vào thi, AI đọc câu hỏi (TTS)
**Mục tiêu:** AI đọc to câu hỏi bằng tiếng Việt, không lỗi, đúng nội dung.
**Các bước:** (1) Cửa sổ ẩn danh mới, đăng nhập Sinh viên. (2) Vào "Phòng thi Viva" (`/Viva/Index`). (3) Nhập EXAM01, bấm "Tạo phiên thi". (4) Hệ thống chuyển tới `/Viva/Exam/{sessionId}`. (5) Quan sát màn hình ban đầu. (6) Bấm "Bắt đầu làm bài thi". (7) Khi AI đọc xong, bấm "Nghe lại câu hỏi" (`btnPlayQuestionAudio`).
**Mong đợi:**
- 2.1 Ban đầu có banner phòng thi sẵn sàng, chưa hiện câu hỏi và chưa tự phát âm thanh.
- 2.2 Sau khi bấm bắt đầu: banner ẩn, hiện khung câu hỏi "EXAM01-Q01: DbContext Lifetime".
- 2.3 Có file âm thanh TTS thật (không phải `/audio/mock-question.mp3`), phát được và đúng nội dung câu hỏi. Ghi lại URL file, kích thước, thời lượng, và log app cho dòng "TTS succeeded" hay "failed".
- 2.4 "Nghe lại câu hỏi" phát lại trọn vẹn.
- 2.5 Đồng hồ đếm ngược chạy lùi từ 90 giây.
- Kiểm tra thêm: nút ghi âm có bấm được trong lúc câu hỏi còn đang phát không? Nếu có, bấm thử và ghi lại thông báo lỗi (đã từng gặp "The answering period is not active or has expired").

### Kịch bản 3: Ghi âm micro và nhận diện giọng nói thật (STT)
**Mục tiêu:** AI nhận diện đúng điều đã nói, không trả câu văn mẫu DbContext giả lập.
**Các bước:** (1) **Chờ AI đọc xong câu hỏi** (client mới gọi BeginAnswering lúc đó). (2) Bấm "Bắt đầu trả lời (Bật mic)". (3) Quan sát sóng âm (canvas). (4) Micro giả phát file WAV đã chuẩn bị. (5) Bấm "Nộp bài trả lời". (6) Chờ xử lý.
**Mong đợi:**
- 3.1 Không có lỗi HTTP 404 model not found, và không có lỗi 503, 400, MIME type trong log (nếu có, ghi nguyên văn thông báo, đã xóa key).
- 3.2 Khung "Bản ghi âm câu trả lời (Speech-to-Text)" hiển thị đúng những từ trong file WAV, không phải đoạn văn mẫu tiếng Anh "Scoped DbContext is instantiated once per HTTP request...".
- 3.3 Badge độ tin cậy (Confidence) hiển thị (ví dụ 90%).
- Kiểm tra thêm: nếu STT lỗi mà giao diện vẫn báo thành công hoặc hiện transcript mock thì ghi rõ "rơi về mock im lặng".

### Kịch bản 4: Câu hỏi phụ của giám khảo AI và hoàn thành bài thi
**Mục tiêu:** AI tạo câu hỏi phụ và hoàn tất buổi thi.
**Các bước:** (1) Sau khi nộp câu chính, quan sát khung "Câu hỏi phụ AI". (2) AI đọc câu hỏi phụ. (3) Ghi âm trả lời tiếp (cùng cách ở kịch bản 3) rồi nộp. (4) Quan sát kết thúc buổi thi.
**Mong đợi:**
- 4.1 Có khung phản biện của giám khảo AI kèm giọng đọc câu hỏi phụ (không phải câu mock cố định).
- 4.2 Sau số lượt quy định, hiện "Hoàn thành bài thi vấn đáp".
- 4.3 Có nút "Xem báo cáo kết quả chi tiết" và bấm vào mở đúng `/StudentReports`, không bị lỗi hay link rỗng.
- Ghi thời gian sinh câu hỏi phụ (giây). Nếu quá 30 giây hoặc báo timeout thì ghi vào báo cáo.
- Chỉ làm một phiên thi trọn vẹn. Nếu kịch bản 3 đã thất bại thì làm 4 theo mức có thể và ghi rõ phần bị chặn.

### Kịch bản 5: Lượt thi mới nhất và điểm Rubric
**Mục tiêu:** Lượt thi mới nhất ở vị trí đầu bên trái; bài thi đã được chấm theo Rubric.
**Các bước:** (1) Đăng nhập lại Giảng viên. (2) Vào `/ClassReports`, chọn sinh viên vừa thi để vào `/ClassReports/StudentDetail?...`. (3) Quan sát: dãy nút lượt thi, chỉ số ĐIỂM TRUNG BÌNH (DEC-05), điểm tổng kết, khối 4 (nhận xét AI), khối 5 (tiêu chí Rubric).
**Mong đợi:**
- 5.1 Lượt thi mới nhất nằm đầu bên trái và tự được chọn xem.
- 5.2 Không còn "Chưa chấm - Đang chờ chấm thi"; điểm trung bình và điểm tổng kết là điểm đã chấm (ví dụ 8.5/10 - ĐẠT CHUẨN).
- 5.3 Khối 4 có Điểm mạnh, Điểm cần cải thiện và AI Feedback.
- 5.4 Khối 5 có 3 huy hiệu xanh: Vòng đời Scoped, Quản lý Entity Tracker và tránh Memory Leak, An toàn đa luồng, kèm mô tả chi tiết.

**Lưu ý quan trọng khi đánh giá kịch bản 1 (mục 1.2) và 5 (5.2 đến 5.4):** theo main hiện tại, dữ liệu mock đã bị gỡ (#57) và việc chấm điểm tự động chưa được cài đặt, nên trạng thái đúng của dự án là "Chưa chấm" (Pending). Nếu các mong đợi này không thấy trên trang thì ghi **"CHƯA CÓ TÍNH NĂNG"** thay vì FAIL, và nêu rõ tính năng nào còn thiếu (Rubric trong LecturerDashboard, chấm điểm AI, nhận xét AI). Đừng cố làm cho chúng PASS bằng cách sửa dữ liệu.

## 4. Báo cáo kết quả
Viết báo cáo vào `docs/TestReports/AIVES_ClaudeCode_Test_<ngày>.md` (thư mục này không đưa vào git; kiểm tra bằng `git check-ignore`, nếu chưa bị ignore thì viết vào thư mục tạm của máy và báo đường dẫn). Gồm:
1. Môi trường: commit đang chạy (`git rev-parse --short HEAD`), cổng, AI thật bật/tắt, routing STT/TTS/LLM (tên hồ sơ và tên model, không có key).
2. Bảng kết quả: mỗi mục mong đợi (1.1 đến 5.4) một dòng gồm Kết quả (PASS / FAIL / CHƯA CÓ TÍNH NĂNG / KHÔNG CHẠY ĐƯỢC), Bằng chứng (đường dẫn ảnh hoặc trích văn bản), Ghi chú.
3. Lỗi tìm thấy: mỗi lỗi một mục gồm bước tái hiện, kết quả thực tế, kết quả mong đợi, log liên quan (đã xóa key).
4. Danh sách phiên thi đã tạo (sessionId) để Kien dọn.
5. Tóm tắt 5 dòng: số PASS, FAIL, CHƯA CÓ TÍNH NĂNG, và việc nên sửa trước.
Tắt bản :5071 khi xong. Không commit, không sửa code.
