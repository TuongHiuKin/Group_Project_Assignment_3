# AIVES – Handoff phiên Claude Code (23–24/10/2026)

File này tóm tắt toàn bộ những gì Claude Code đã làm trong phiên: kiểm thử, phát hiện lỗi, sửa code, kết quả test và việc còn lại. File chưa được đưa vào git.

---

## 1. Tóm tắt nhanh

- Đã chạy kịch bản kiểm thử `docs/AIVES_ClaudeCode_Test_Script_v2.md` 3 lần bằng Playwright (Chromium, micro giả).
- Đã làm xong 5 việc của mục **X** trong `docs/Claude_Communication.md` và sửa thêm 2 lỗi phát hiện khi test lại (**B9**, **B10**).
- Tất cả nằm trên nhánh `feature/rubric-grading` (7 commit), **chưa push, chưa PR, chưa merge**.
- `dotnet build`: 0 lỗi, 0 cảnh báo. `dotnet test`: **433 passed, 0 failed, 24 skipped** (24 test SQL thật tắt sẵn).
- Kịch bản 3–4 (STT thật, câu hỏi phụ) **chưa kiểm chứng được trên trình duyệt** vì Gemini STT liên tục 503 / treo tới timeout.

---

## 2. Trạng thái git

| Vị trí | Nhánh | Commit |
|---|---|---|
| `D:\School\AIVES_AI_powered_Viva_Exam_System` (thư mục chính) | `kien/wip-2026-10-23` | `6f6b3f8` |
| `D:\School\AIVES-rubric-grading` (git worktree) | `feature/rubric-grading` | `5ea65b5` |

- `kien/wip-2026-10-23` tách từ `main` (`894bcdc`), chứa đúng 1 commit `6f6b3f8` = 21 file Kien sửa dở trước phiên (snapshot nguyên trạng).
- `feature/rubric-grading` tách từ `6f6b3f8`.
- Thư mục chính còn 4 file chưa theo dõi (không đụng tới): `docs/AIVES_Architecture_2026-10-21.drawio`, `docs/AIVES_ClaudeCode_Context_Prompt.md`, `docs/AIVES_ClaudeCode_Test_Script.md`, `docs/AIVES_ClaudeCode_Test_Script_v2.md` (file v2 đã được Claude sửa mục 5.2–5.4).
- `docs/Claude_Communication.md` bị `.gitignore` bỏ qua → mục **S. Kết quả vòng 9** (đã ghi kết quả từng việc) không nằm trong commit nào.

### Danh sách commit trên `feature/rubric-grading`
```
5ea65b5 fix(reports): attempt tabs on StudentDetail switch again                       (B9)
e2a802c fix(viva): pause the answering clock while STT runs                             (B10)
c85983d fix(viva-ui): lock mic until BeginAnswering and announce browser voice fallback (X.5)
46ae1a6 fix(reports): ClassReports student link opens the newest attempt                (X.4)
73cd455 fix(ai): valid Gemini default models and ListModels validation on routing save  (X.3)
4cc3ad0 fix(viva): retry Gemini 429/503 and surface follow-up failures instead of completing (X.2)
781e6d9 feat(grading): rule-based keyword rubric grading replaces hardcoded scores      (X.1)
6f6b3f8 chore(wip): snapshot local changes before rubric grading work                  (nhánh WIP)
```

---

## 3. Thay đổi cấu hình đã làm (không phải code)

- Trên `/Admin/AiSettings`, routing **TTS** đổi từ `gemini-3.8-flash-preview-tts` (lỗi 404) sang `gemini-3.8-flash-tts`. STT/LLM, hồ sơ và key không đổi.
- Routing hiện tại: STT `Gemini / gemini-3.8-flash`, TTS `Gemini / gemini-3.8-flash-tts` (voice Puck), LLM `Gemini / gemini-3.8-flash`.
- Claude không đọc, in hay đặt API key; không mở `App_Data/ai-credentials.json`.

---

## 4. Kiểm thử lần 1 (code gốc: `894bcdc` + working tree chưa commit)

Vai trò tài khoản test (chỉ ghi email): `admin@AIVESSystem.org` = Admin, `kin@gmail.com` = Sinh viên (Staff), `kinn@gmail.com` = Giảng viên.

Kết quả: 14/19 PASS, 5 FAIL (4.1, 5.1, 5.2, 5.3, 5.4). Lỗi tìm thấy:

| Mã | Lỗi | Trạng thái |
|---|---|---|
| B1 | Routing TTS trỏ model không tồn tại (404); mặc định `gemini-1.5-flash` / `gemini-2.0-flash` sai trong adapter, credential store, test | Đã sửa (X.3) |
| B2 | TTS lỗi nhưng client im lặng dùng giọng trình duyệt | Đã sửa (X.5) |
| B3 | Nút ghi âm bấm được trước khi gọi `BeginAnswering` | Đã sửa (X.5) |
| B4 | LLM 503 làm phiên tự kết thúc ở lượt 1/2, không báo lỗi | Đã sửa (X.2) |
| B5 | Điểm 8.5, nhận xét AI, 3 huy hiệu Rubric viết cứng, không dựa vào câu trả lời | Đã sửa (X.1) |
| B6 | STT chậm (43 s cho ~15 s âm thanh) | Ghi nhận, phụ thuộc Gemini |
| B7 | Link `/ClassReports` mở Lượt 1 thay vì lượt mới nhất | Đã sửa (X.4) |
| B8 | "8.50 / 10 CHƯA ĐẠT" mâu thuẫn "ĐẠT CHUẨN" | Đã sửa (X.1, `ClassReportService` gán `IsPassed`) |

Kiểm chứng thêm: Gemini STT **nhận được `audio/webm`** và chép đúng tiếng Việt (nghi vấn webm trong mục W đã được giải tỏa).

---

## 5. Mục X – chi tiết từng việc

### X.1 Chấm điểm theo Rubric từ khóa (`781e6d9`)
- Mới: `IRubricGradingService`, `RubricGradingService`, `RubricCatalog` (nguồn Rubric duy nhất EXAM01-Q01..Q03: id, tên, mô tả, từ khóa Việt/Anh, trọng số 3.5/3.5/3.0), `RubricModels.cs`.
- Luật: chuẩn hóa transcript (chữ thường, bỏ dấu kể cả đ, bỏ dấu câu); tiêu chí đạt khi khớp trọn cụm từ khóa; điểm câu = tổng trọng số đạt × 10 / tổng trọng số; ngưỡng đạt 6.0 từ một hằng `ExamReportRepository.DefaultPassThresholdScore`. Câu trả lời có dưới 3 từ mới so với câu hỏi (rỗng / đọc lại câu hỏi) = 0 điểm, "Chưa có câu trả lời phù hợp".
- `ExamReportRepository`: bỏ `CreateGradedQuestionResult`, 8.5, lời khen cố định, điều kiện `studentId == "10"`; chỉ `Graded` khi phiên Completed và có transcript.
- `LecturerDashboard` đọc Rubric từ catalog; giao diện ghi "Chấm tự động theo Rubric từ khóa", "Độ tin cậy (ước lượng)".

### X.2 Lỗi 429/503 và rơi về mock im lặng (`4cc3ad0`)
- `GeminiRetryPolicy`: thử lại tối đa 2 lần (1 s, 3 s) với HTTP 429/503 cho STT, TTS, LLM.
- Orchestrator: LLM lỗi → phiên về `TranscriptReady`, phát `VivaError` `FOLLOWUP_GENERATION_FAILED` (tiếng Việt, cho thử lại). Trang thi có khung "Thử lại" / "Kết thúc bài thi" (handler `RetryFollowUp`, `FinishSession`).
- `AddAiServices`: khi không resolve được service thật và `UseMock=false`, `AllowMockFallback=false` → trả `Unavailable*Service` với lý do tiếng Việt thay vì mock.

### X.3 Model mặc định (`73cd455`)
- `GeminiModels`: STT/LLM `gemini-3.8-flash`, TTS `gemini-3.8-flash-tts`, voice `Puck`.
- Lưu routing: kiểm tra model Gemini theo năng lực (TTS phải có "tts") và theo `ListModels` thật, báo lỗi tại trang kèm gợi ý.
- Lưu hồ sơ không ghi đè model còn hợp lệ.

### X.4 Link `/ClassReports` (`46ae1a6`)
- Bỏ `attemptOrdinal` khỏi link sang `StudentDetail` → mở lượt mới nhất.

### X.5 Trang thi (`c85983d`)
- Nút ghi âm khóa khi đang đọc câu hỏi/câu hỏi phụ; chỉ mở sau khi `BeginAnswering` thành công.
- TTS không có audio → toast "Giọng AI lỗi, đang dùng giọng trình duyệt."

### B10 STT chậm làm hết giờ trả lời (`e2a802c`)
- Đồng hồ dừng khi STT chạy; STT lỗi thì trả lại đúng số giây còn lại lúc nộp (`VivaTimerManager.Resume`, không cộng thêm).

### B9 Tab lượt thi trên StudentDetail không chuyển (`5ea65b5`)
- Bootstrap 5.3.3 chỉ chuyển pill trong `[role="tablist"]`; đã bọc nút, thêm `role="tab"`, `aria-*`, style `btn-outline-primary` + `.active`.

---

## 6. Kiểm thử lại trên `feature/rubric-grading`

| Mục | Lần 2 (`c85983d`) | Lần 3 (`5ea65b5`) |
|---|---|---|
| 1.1–1.4 | PASS (1.2: tiêu đề khối đổi thành "Tiêu chí Chấm điểm Vấn đáp theo Rubric") | – |
| 2.1–2.5 | PASS | PASS |
| Nút ghi âm khi đang đọc câu hỏi | PASS (khóa) | PASS |
| 3.1 STT | FAIL – 503, 503, timeout 90 s | FAIL – 503 rồi treo tới timeout, 2 lần nộp |
| 3.2, 3.3, 4.1–4.3 | KHÔNG CHẠY ĐƯỢC | KHÔNG CHẠY ĐƯỢC |
| Lỗi STT hiện rõ, không mock | PASS | PASS |
| B10 (giữ thời gian khi STT lỗi) | – (lỗi, phiên TimedOut) | PASS: còn 00:53 rồi 00:40, ghi âm lại được |
| 5.1 lượt mới nhất | PASS | PASS |
| 5.2–5.4 chấm Rubric | PASS: lượt đọc lại câu hỏi = 0.0/10 CHƯA ĐẠT, 3 tiêu chí CHƯA ĐẠT, "Chưa có câu trả lời phù hợp…" | PASS |
| B9 chuyển tab | FAIL (phát hiện) | PASS |

Chưa kiểm chứng trên trình duyệt: câu hỏi phụ, khung Thử lại/Kết thúc, toast giọng dự phòng, điểm cao cho câu trả lời đúng ý.

---

## 7. Phiên thi Claude đã tạo (Kien cần dọn)

| sessionId | Trạng thái | Lần chạy |
|---|---|---|
| `11b7a16c-9cbc-48d3-b069-665a1d8c2657` | TimedOut | Lần 1 (TTS 404) |
| `9e1949fe-22a9-4b54-8fa1-91dacbc94da6` | Completed | Lần 1 |
| `02f9ddd8-1ee4-478f-b3d2-4ab3598c4ea1` | TimedOut | Lần 2 |
| `160df968-3edd-4057-ae4b-bd23dc1f02db` | Bỏ dở (TimedOut) | Lần 3 |

---

## 8. Việc còn lại / đề xuất

1. **Timeout Gemini STT** (chưa làm): timeout 90 s hiện áp cho cả chuỗi retry nên một lần gọi treo ăn hết thời gian. Đề xuất đặt timeout **riêng cho từng lần gọi** (~20–25 s), giữ trần tổng 90 s.
2. **Nhà cung cấp dự phòng cho STT** (chưa làm): Gemini lỗi thì chuyển sang Whisper qua adapter OpenAI-compatible, ví dụ Groq `whisper-large-v3` (chưa kiểm chứng trên máy Kien) hoặc OpenAI Whisper.
3. **DeepSeek cho LLM** (cấu hình, không cần code): hồ sơ OpenAI-compatible, Base URL `https://api.deepseek.com/v1`, chỉ bật LLM, model **`deepseek-chat`** (không dùng `deepseek-reasoner` vì chậm). DeepSeek không có STT/TTS. Lưu ý: trang cài đặt chỉ kiểm tra tên model với hồ sơ Gemini.
4. Mục W chưa làm: tự đổi routing khi thêm hồ sơ mới (W.3), `thinkingBudget = 0` cho LLM.
5. Chạy lại Kịch bản 3–4 khi Gemini bớt quá tải (1 phiên).
6. Khi Kien cho phép: tạo **draft PR** từ `feature/rubric-grading`.

---

## 9. Lưu ý vận hành

- **Chạy app từ worktree không giải mã được `App_Data`**: `Program.cs` dùng `AddDataProtection()` mặc định, khóa gắn với ContentRootPath. Khi test, Claude đã tạm `git switch --detach <commit>` ở thư mục chính, chạy `dotnet run --project StudentNameMVC --urls http://localhost:5070`, rồi trả về `kien/wip-2026-10-23`.
- Build/test: `dotnet build AIVESSystem.sln`, `dotnet test AIVESSystem.sln` (thư mục có 2 file solution nên phải chỉ rõ `.sln`).
- `appsettings.Development.json`: `UseMock=false`, `AllowMockFallback=false`, timeout STT 90 s, TTS 20 s, LLM 35 s.
- Toast `#vivaErrorToast` có sẵn chữ mặc định "Đã xảy ra sự cố không xác định." trong HTML (ẩn) – không phải lỗi.

---

## 10. Vị trí bằng chứng (thư mục tạm, có thể bị xóa – nên chép ra nếu cần giữ)

`C:\Users\ANKIN\AppData\Local\Temp\claude\D--School-AIVES-AI-powered-Viva-Exam-System\687a5226-e53e-46b3-8fba-5d779cccb494\scratchpad\`

- Báo cáo: `AIVES_ClaudeCode_Test_2026-10-23.md` (lần 1), `AIVES_ClaudeCode_Test_2026-10-23_run2.md` (lần 2 + mục 6 lần 3).
- Ảnh chụp: `shots\` (lần 1), `shots-run2\`, `shots-run3\`.
- Log app: `app5070.log`, `app5070-run2.log`, `app5070-run3.log`.
- Output Playwright: `s234*.out`; script: `pw-*.js`.
