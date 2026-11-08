# Cấu hình DeepSeek API cho AIVES

> Có **khối "Kết nối DeepSeek" riêng** trên trang `/Admin/AiSettings`: dán API key là xong,
> hệ thống tự tạo hồ sơ, tự định tuyến LLM + Grading và bật AI thật.

---

## 1. DeepSeek hỗ trợ những gì

| Năng lực | DeepSeek | Dùng cho AIVES |
| :--- | :--- | :--- |
| **LLM (chat)** | ✅ `deepseek-chat`, `deepseek-reasoner` | Câu hỏi xoáy (Follow-up) |
| **Grading** | ✅ dùng chung endpoint chat | Chấm điểm câu trả lời (Nhóm 4) |
| **STT (Speech-to-Text)** | ❌ không có endpoint audio | Phải dùng provider khác (Gemini / Whisper) |
| **TTS (Text-to-Speech)** | ❌ không có endpoint audio | Phải dùng provider khác, hoặc bật `UseBrowserSpeechSynthesis` |

**Kết luận:** DeepSeek phù hợp để chấm điểm + sinh câu hỏi xoáy. STT/TTS vẫn phải cấu hình riêng —
khối DeepSeek **không có** checkbox STT/TTS nên không thể cấu hình sai.

> **Không dùng `deepseek-reasoner`** cho grading: model này sinh chuỗi suy luận dài, dễ vượt timeout 30 giây
> và làm hỏng định dạng JSON. Form sẽ **chặn** nếu chọn model này.

---

## 2. Cách dùng: khối "Kết nối DeepSeek" (3 bước)

### Bước 1 — Dán API key
1. Đăng nhập Admin → `/Admin/AiSettings`.
2. Tìm khối **"Kết nối DeepSeek"** (viền xanh, ngay dưới "Trạng thái hoạt động").
3. Dán key vào ô **API Key DeepSeek** (lấy tại <https://platform.deepseek.com/api_keys>).

### Bước 2 — Chọn năng lực
- **Model:** để `deepseek-chat` (mặc định). `deepseek-reasoner` sẽ bị từ chối.
- **Năng lực dùng DeepSeek:** tick `LLM` và/hoặc `Grading`.

### Bước 3 — Lưu
- Bấm **Kiểm tra key** trước (tuỳ chọn): hệ thống gọi `GET https://api.deepseek.com/v1/models`
  và hiển thị danh sách model thật.
- Bấm **Kết nối DeepSeek** (hoặc **Lưu thay đổi** nếu đã kết nối).

Hệ thống tự động:
- Tạo hồ sơ `Id = deepseek`, `Kind = OpenAICompatible`, `BaseUrl = https://api.deepseek.com/v1`
- Định tuyến `LLM` và/hoặc `Grading` sang hồ sơ này
- Bật `UseRealAi = true`
- **Không đụng** tới STT/TTS đang cấu hình

Sau khi lưu, khối hiển thị badge **"Đã kết nối"** cùng nhãn `Grading đang dùng DeepSeek` / `LLM đang dùng DeepSeek`.

### Ngắt kết nối
Bấm **Ngắt kết nối** ở góc phải khối. Hồ sơ DeepSeek bị xoá và định tuyến LLM/Grading được gỡ
(STT/TTS giữ nguyên).

---

## 3. Cách thay thế: dùng form "Thêm nhà cung cấp" chung

Nếu muốn cấu hình thủ công (ví dụ để đặt tên riêng):

| Trường | Giá trị |
| :--- | :--- |
| Tên nhà cung cấp | `DeepSeek` |
| Loại (Kind) | `OpenAI-Compatible` |
| Base URL | `https://api.deepseek.com/v1` |
| API Key | key DeepSeek |
| Organization ID | để trống |

Khi gõ Base URL chứa `deepseek`, form **tự bỏ tick STT/TTS** và chỉ giữ `LLM` + `Grading`.
Sau đó định tuyến LLM/Grading sang hồ sơ này (hệ thống tự điền `deepseek-chat`).

---

## 4. Kiểm chứng bằng lệnh (không cần app)

```powershell
$key = "sk-..."   # key DeepSeek của bạn

# 1. Liệt kê model
curl.exe -s https://api.deepseek.com/v1/models -H "Authorization: Bearer $key"

# 2. Gọi thử chat completion
curl.exe -s https://api.deepseek.com/v1/chat/completions `
  -H "Authorization: Bearer $key" `
  -H "Content-Type: application/json" `
  -d '{\"model\":\"deepseek-chat\",\"messages\":[{\"role\":\"user\",\"content\":\"Xin chào\"}],\"max_tokens\":50}'
```

Nếu 2 lệnh trên trả JSON bình thường thì AIVES sẽ gọi được.

---

## 5. Kiểm chứng trong code (test tự động)

```powershell
# Cấu hình UI: lưu key, kiểm tra key, ngắt kết nối, không đụng STT/TTS
dotnet test AIVES.Tests/AIVES.Tests.csproj --filter "FullyQualifiedName~DeepSeekSettingsPageTests"

# Hợp đồng HTTP với DeepSeek: endpoint, Bearer auth, json_object, lỗi 402
dotnet test AIVES.Tests/AIVES.Tests.csproj --filter "FullyQualifiedName~DeepSeekIntegrationTests"
```

| Test | Nội dung |
| :--- | :--- |
| `SaveDeepSeek_creates_the_profile_and_routes_llm_and_grading` | Tạo hồ sơ + định tuyến + bật AI thật |
| `SaveDeepSeek_never_touches_stt_or_tts_routing` | STT/TTS Gemini giữ nguyên |
| `SaveDeepSeek_rejects_reasoner_model_for_grading` | Chặn `deepseek-reasoner` |
| `SaveDeepSeek_requires_a_key_for_a_new_profile` | Bắt buộc key khi chưa có hồ sơ |
| `TestDeepSeek_uses_the_typed_key_and_reports_models` | Nút "Kiểm tra key" dùng key vừa dán |
| `RemoveDeepSeek_deletes_the_profile_and_clears_routing` | Ngắt kết nối dọn routing |
| `LoadViewModelData_populates_the_deepseek_card_state` | Không echo key ra form |
| `Get_AiSettings_RendersDeepSeekConnectCard` | Trang render đúng HTML (HTTP 200) |
| `Grading_posts_to_deepseek_chat_completions_with_bearer_auth_and_json_mode` | Hợp đồng HTTP |
| `Grading_surfaces_a_DeepSeek_api_error_without_leaking_the_key` | Lỗi 402, không lộ key |

---

## 6. Xử lý sự cố

| Triệu chứng | Nguyên nhân | Cách sửa |
| :--- | :--- | :--- |
| `HTTP 404` khi chấm điểm | Base URL thiếu `/v1` hoặc dư `/chat/completions` | Khối DeepSeek luôn dùng đúng URL; nếu cấu hình tay, dùng `https://api.deepseek.com/v1` |
| `HTTP 400 model not found` | Model sai tên | Dùng `deepseek-chat` (không phải `deepseek` / `deepseek-v3`) |
| `HTTP 402 Insufficient Balance` | Hết số dư tài khoản | Nạp tiền, hoặc bật `AllowMockFallback` để dùng `RubricGradingService` |
| Grading treo tới 30s rồi fallback | Dùng `deepseek-reasoner` | Form đã chặn; đổi sang `deepseek-chat` |
| Báo "Model ... sinh chuỗi suy luận dài" | Chọn `deepseek-reasoner` | Chọn lại `deepseek-chat` |
| Chấm điểm ra 0 điểm / nhãn `KeywordFallback` | AI lỗi, hệ thống tự chấm từ khóa | Xem log `AI grading threw exception...`; kiểm tra key/hạn mức |
| Không thấy DeepSeek trong dropdown định tuyến | Hồ sơ chưa tick năng lực | Mở khối DeepSeek, tick `LLM`/`Grading` rồi lưu lại |

**Lưu ý:** nếu AI lỗi, `GradingOrchestrator` **tự động** rơi về `RubricGradingService` (chấm từ khóa)
nên bài thi vẫn có điểm, chỉ khác nhãn `AiProvider = "KeywordFallback"`. Đây là thiết kế cố ý, không phải bug.

---

## 7. Bảo mật

- API key lưu trong `StudentNameMVC/App_Data/ai-credentials.json` (mã hoá bằng Data Protection, gitignored).
- Ô nhập key **luôn rỗng** sau khi lưu; chỉ hiển thị gợi ý dạng `…abcd`.
- Không đặt key vào `appsettings.json` hay commit lên git.
- Log đã lọc qua `AiKeySanitizer.Sanitize(...)`, nhưng vẫn không nên bật `Debug` cho `System.Net.Http` khi demo.

