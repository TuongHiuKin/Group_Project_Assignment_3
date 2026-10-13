# Bàn giao SignalR — RT-01 đến RT-06

Ngày: 13/10/2026. Snapshot bắt đầu: `main`, commit `70b7d14`.
Working tree ban đầu sạch. Nhánh bàn giao: `hao/asm2-signalr-rt-01-06`.
User đã yêu cầu commit và push toàn bộ phần SignalR lên một nhánh mới; không tạo PR trong yêu cầu này.
User đã cho phép sửa shared files trong cuộc hội thoại này; vẫn cần Tech Lead review khi tích hợp.

## 1. Kết quả và phạm vi

Đã triển khai module SignalR, timer và recovery, kèm kiểm thử qua application startup thật.
Các test dùng Hub, middleware authorization, publisher, BLL recovery/timer và orchestrator thật;
repository, AI và authentication identity trong integration test là fixture.
Không đưa fake Viva repository, fake identity hoặc fake AI vào production DI.
Không triển khai Group 4, không sửa schema/DbContext, không chạy migration, seed hay ghi SQL.

| Task / issue theo ảnh | Đầu ra | Trạng thái trong checkout này |
| --- | --- | --- |
| RT-01 / #13 | `VivaHub`, JoinSession, LeaveSession, authenticated group enrollment | Code và test đạt; route được bật khi đủ dependency |
| RT-02 / #14 | Dùng lại `IVivaClientEvents` có sẵn; sáu event strongly typed; string enum JSON | Đạt; không tạo interface trùng |
| RT-03 / #15 | `VivaRealtimePublisher`, gửi đúng group | Code và test đạt |
| RT-04 / #16 | Timer phía server, timeout, nộp sớm, chống reset giờ | Đạt trong một tiến trình server; deadline chưa lưu SQL |
| RT-05 / #17 | Rejoin, snapshot câu hỏi, replay transcript/follow-up và thời gian riêng cho caller | Đạt khi browser reconnect vào cùng tiến trình server |
| RT-06 / #18 | Unit tests + SignalR client integration tests | 27 test realtime đạt |

**Sẵn sàng review phần SignalR và tích hợp với đồng đội. Chưa xác nhận demo toàn hệ thống dùng SQL/AI thật.**

## 2. File mới

BLL:
- `AIVES.BLL/Interfaces/VivaInterview/IVivaRealtimeSessionService.cs`: shared BLL contract mới cho recovery/timer;
  Hub, publisher và worker chỉ gọi BLL qua interface này.
- `AIVES.BLL/Models/VivaInterview/VivaSessionSnapshotDto.cs`: snapshot trả về khi join/rejoin.
- `AIVES.BLL/Services/VivaInterview/VivaRealtimeSessionService.cs`: đọc trạng thái/câu hỏi qua repository,
  Ready transition, BeginAnswering và timeout persistence trước event.
- `AIVES.BLL/Services/VivaInterview/VivaTimerManager.cs`: singleton chỉ giữ deadline và khóa;
  không giữ repository, DbContext, Hub hoặc cancellation token của connection.

Presentation:
- `StudentNameMVC/Hubs/VivaHub.cs`: kiểm tra claims/quyền qua BLL, group enrollment, recovery caller,
  BeginAnswering; không có phương thức cho client tự broadcast hoặc tự chọn thời lượng.
- `StudentNameMVC/Hubs/VivaSessionGroup.cs`: chuẩn tên group `viva-session-{sessionId:D}`.
- `StudentNameMVC/Services/VivaRealtimePublisher.cs`: scoped publisher dùng typed IHubContext.
- `StudentNameMVC/Services/VivaTimerWorker.cs`: mỗi giây tạo scope mới cho BLL/repository.
- `StudentNameMVC/VivaRealtimeExtensions.cs`: AddVivaRealtime và MapVivaRealtime.

Tests:
- `AIVES.Tests/Realtime/RealtimeTestDoubles.cs`: fixture repository/AI, manual clock, publisher recorder.
- `AIVES.Tests/Realtime/VivaTimerTests.cs`: deadline, race, early/late submission, failed save, retry và restart.
- `AIVES.Tests/Realtime/VivaRealtimePublisherTests.cs`: group, completion persistence, cancellation.
- `AIVES.Tests/Realtime/VivaHubIntegrationTests.cs`: WebApplicationFactory + HubConnection;
  authorization, isolation, leave, reconnect, payloads và BLL interview flow.

Tài liệu: file bàn giao này.

## 3. File có sẵn đã sửa — cần chú ý khi merge

| File | Thay đổi / lý do |
| --- | --- |
| `StudentNameMVC/Program.cs` | Đăng ký và map module theo `VivaInterview:Enabled`; public partial Program để integration test |
| `AIVES.BLL/Services/VivaInterview/VivaSessionOrchestrator.cs` | Nhận optional timer manager; serialize nộp bài với timeout; kiểm tra deadline/câu hỏi; dừng timer sau persistence; STT failure trả lại deadline gốc nếu còn giờ |
| Cùng orchestrator | Chặn restart phiên đang thi/terminal; sort câu đầu thống nhất với snapshot; giữ FK câu chính khi nộp follow-up; chặn turn thuộc phiên khác; propagate TTS cancellation; dừng timer khi hoàn thành |
| `AIVES.Tests/AIVES.Tests.csproj` | Thêm Microsoft.AspNetCore.Mvc.Testing và Microsoft.AspNetCore.SignalR.Client 8.0.30, cùng dòng .NET 8 / EF Core 8.0.30 hiện có |
| `docs/ASM2-CONTRACTS.md` | Đồng bộ namespace Hub thực và JSON SessionCompleted với interface/RT-02 hiện có; bổ sung contract recovery/timer |

Không đổi chữ ký của `IVivaSessionOrchestrator`, `IVivaRealtimePublisher`, `IVivaClientEvents` hoặc repository interfaces.
Shared interface mới `IVivaRealtimeSessionService` nằm trong phạm vi user đã cho phép sửa và cần Tech Lead review.
Optional timer constructor parameter giữ compatibility với test cũ; khi bật module bằng Program,
DI luôn cung cấp VivaTimerManager và kiểm tra deadline được thực thi.

## 4. Bật module khi dependency thật được bàn giao

Hiện `AddAivesDataAccess` chưa đăng ký repository Viva; AI adapters cũng chưa được đăng ký.
Do đó flag mặc định false để ứng dụng Account/Group 6 hiện có vẫn khởi động.
Nếu chưa bật flag, `/hubs/viva` chưa được map — HTTP 404 là hành vi dự kiến.

Tech Lead cần đăng ký implementation thật cho:

```text
IVivaSessionRepository   Scoped
IVivaQuestionRepository  Scoped
IVivaTurnRepository      Scoped
ISpeechToTextService
ITextToSpeechService
IFollowUpGeneratorService
```

Repository phải hoàn tất SaveChangesAsync trước khi Task ghi dữ liệu trả thành công;
GetWithDetailsAsync phải tải các Turns thuộc đúng session.
Sau đó bật `VivaInterview:Enabled=true` bằng cấu hình local/deployment,
ví dụ biến môi trường PowerShell trước khi chạy ứng dụng:

```powershell
$env:VivaInterview__Enabled = 'true'
dotnet run --project StudentNameMVC
```

Không bật flag rồi đăng ký fake repository để gọi là production đã hoàn tất.
Program gọi AddVivaRealtime và AddVivaInterviewServices; không cần gọi lại hai hàm này trong startup.
Không thêm singleton repository để khắc phục lỗi DI.

## 5. Hợp đồng để frontend tích hợp

Route: `/hubs/viva`. Authentication: cookie có `ClaimTypes.NameIdentifier`;
BLL hiện chỉ cho phép session owner. Không tự cho mọi Lecturer/Admin vào mọi phòng.
Quyền giám khảo cần Tech Lead bổ sung trong BLL khi có mapping phân công được xác nhận.

Hub methods:

```text
JoinSession(Guid sessionId) -> VivaSessionSnapshotDto
LeaveSession(Guid sessionId) -> void
BeginAnswering(Guid sessionId) -> VivaTimeRemainingDto
```

JoinSession trả snapshot gồm `status`, `currentQuestion`, `latestTranscript`, `currentFollowUp`, `timer`.
Snapshot không tạo session mới, không gọi StartSession và không gọi AI để tái tạo câu hỏi.
Created -> Ready được ghi qua repository trước khi trả Ready.
Rejoin phát event riêng cho caller; không thông báo dư thừa đến người khác.
Client không thuộc group chưa được nhận events chỉ vì có cùng user ID.

Luồng frontend:
1. Đăng ký event listeners trước khi start connection.
2. Start connection; invoke JoinSession; render snapshot theo `status.state`.
3. Nút bắt đầu thi gọi PageModel/BLL StartSessionAsync (owner UI/Lead).
4. Khi TTS kết thúc, invoke BeginAnswering; chỉ cho ghi âm sau khi server chấp nhận.
5. Upload audio qua handler HTTP/Razor Pages có authorization/antiforgery; không upload qua Hub.
6. Nộp `questionId` chính xác từ câu đang hiển thị, kể cả `Q1-FU1`.
   BLL giữ FK đến `Q1` khi lưu lượt trả lời follow-up.
7. Khi reconnect, invoke JoinSession lại; không gọi StartSession/BeginAnswering để reset tiến độ.
8. Khi terminal state hoặc CanRetry=false, khóa ghi âm.

Ví dụ điểm nối tối thiểu cho module SignalR JS hiện có của frontend:

```javascript
const connection = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/viva')
    .withAutomaticReconnect()
    .build();

// renderSnapshot là hàm của frontend, phải render theo state để không phát lại TTS sau Completed.
async function restoreSession() {
    const snapshot = await connection.invoke('JoinSession', sessionId);
    renderSnapshot(snapshot);
}
connection.onreconnected(restoreSession);
await connection.start();
await restoreSession();
// Sau khi TTS kết thúc và người dùng bắt đầu trả lời:
const timer = await connection.invoke('BeginAnswering', sessionId);
```

withAutomaticReconnect không tự retry lần start đầu tiên; frontend xử lý initial connection error.
Không dùng thời gian hoặc duration do client gửi làm nguồn quyết định deadline.
Listeners/replay cần cập nhật theo turnId/questionId, không append trùng transcript khi reconnect.

Events giữ nguyên:

| Event | Payload |
| --- | --- |
| SessionStatusChanged | VivaSessionStatusDto |
| TranscriptReady | VivaTranscriptDto |
| FollowUpGenerated | VivaFollowUpDto |
| TimeRemainingChanged | VivaTimeRemainingDto |
| SessionCompleted | VivaSessionStatusDto, State=Completed |
| VivaError | VivaErrorDto |

JSON camelCase, enum state là string. SessionCompleted **không** chứa redirectUrl/attemptId:
frontend và reporting phải có mapping thật trước khi redirect. Không bịa attemptId từ sessionId.
Typed publisher kiểm tra cancellation trước khi gửi và hủy chờ nếu đang gửi;
việc hủy chờ không thể thu hồi event SignalR đã được đưa vào transport.

## 6. Timer và giới hạn vận hành

- Thời lượng lấy từ question.TimeLimitSeconds phía server.
- Repeated BeginAnswering trả giờ còn lại; không thay deadline.
- Nộp bài và Tick timeout dùng cùng khóa session trong một tiến trình.
- Hết giờ: lưu TimedOut trước, sau đó phát remaining=0 và SessionStatusChanged.
- Nộp sớm: lưu Transcribing thành công rồi dừng timer trước khi chạy STT.
- STT trả IsSuccess=false: khôi phục Answering và deadline gốc nếu còn giờ;
  nếu deadline đã hết thì lưu TimedOut và CanRetry=false. Không cấp lại thời lượng đầy đủ.
- Timer worker tạo scope mới mỗi lần tick; disconnect không hủy timer.
- Deadline nằm trong RAM của **một server process**, không phải SQL.
  Sau server restart, phiên Answering thiếu deadline bị từ chối nộp/bắt đầu lại;
  Join trả VivaError ANSWERING_DEADLINE_UNAVAILABLE, CanRetry=false.
  Khôi phục sau restart/multi-node cần DB member bổ sung persisted per-turn deadline và concurrency contract.
- Timestamp snapshot được dựng từ các timestamp hiện có trong entity;
  chưa có persisted UpdatedAtUtc riêng cho mỗi state transition.
- Audio câu chính được khôi phục từ VivaQuestion.AudioUrl. Nếu StartSession tạo URL TTS
  nhưng đồng đội chưa lưu URL này, snapshot có thể trả AudioUrl=null; không gọi AI lại khi join.
- Orchestrator hiện có giới hạn 2 turns và lấy câu chính đầu tiên; module này không viết lại
  chính sách chuyển nhiều câu chính hay hệ thống chấm điểm.
- Exception/cancellation ngoài kết quả IsSuccess=false của AI adapter, cùng recovery sau lỗi lưu
  transcript giữa chừng, vẫn thuộc phần resilience orchestrator/AI của Tech Lead và AI member.
  Test realtime không chứng minh những trường hợp đó đã được xử lý đầy đủ.

## 7. Bằng chứng kiểm thử

Đã chạy trên .NET SDK 10.0.401, project target net8.0:

```powershell
dotnet restore AIVESSystem.sln
dotnet build AIVESSystem.sln --configuration Release --no-restore
dotnet test AIVESSystem.sln --configuration Release --no-build --no-restore --filter 'Category!=SqlServerWrite&Category!=SqlServerReadOnly' --logger 'trx;LogFileName=signalr-regression.trx' --results-directory TestResults/SignalR
```

Kết quả: build **0 warnings, 0 errors**; **125 passed, 0 failed, 0 skipped** trong phạm vi đã lọc.
Có **27 test realtime mới**. Các test SQL Server bị loại khỏi lượt chạy, không được tính là passed.
TRX local: `TestResults/SignalR/signalr-regression.trx` (được gitignore, không commit).

Phạm vi đã kiểm chứng:
- Authorization middleware từ chối anonymous connection.
- Sai owner hoặc thiếu identity claim không join được và không nhận transcript.
- Hai session không nhận chéo transcript; LeaveSession ngừng nhận event.
- Reconnect reauthorize và giữ deadline; thu hồi quyền trước rejoin bị chặn.
- Snapshot phục hồi câu hỏi, audio có lưu, transcript và follow-up từ repository fixture.
- Join/recovery không phát status dư đến peer đang online.
- Hosted worker thực sự timeout sau khi sinh viên disconnect; state đã lưu trước event.
- Sáu event roundtrip qua SignalR client đúng payload/string enum.
- Luồng thật Join -> Ready -> Start -> BeginAnswering -> Submit -> Follow-up -> Submit -> Completed,
  dùng orchestrator thật và fake repository/AI.
- Không phát completion trước khi đã lưu; cancellation publisher; race timeout/nộp bài;
  late/wrong question rejection; save failure; STT false retry giữ deadline.

Integration test dùng Long Polling qua TestServer. Chưa kiểm chứng WebSocket trên IIS/Kestrel,
cookie login thật, microphone/DOM/audio playback, SQL persistence/FK/concurrency hoặc AI APIs thật.

Các kiểm tra cần chạy lại khi teammate tích hợp:
1. Login hai sinh viên thật; mở hai phiên khác nhau và thử sessionId của người khác.
2. Frontend nhận transcript/follow-up, countdown và completion đúng phòng.
3. Tắt mạng rồi bật lại; snapshot đúng câu hỏi và giờ, không reset bài thi.
4. Nộp sớm và hết hạn; xác minh state/transcript lưu SQL trước khi nhận event.
5. Thử WebSocket tại môi trường triển khai.

## 8. Trước khi commit và push

Nhánh `hao/asm2-signalr-rt-01-06` được tạo từ `main` tại `70b7d14`, khớp `origin/main` sau fetch.
Tên Hào được chuẩn hóa thành `hao` theo quy ước lowercase ASCII của nhóm.
AGENTS mặc định yêu cầu 1 issue / 1 branch / 1 PR; trong lần bàn giao này user đã yêu cầu
commit và push phần SignalR RT-01–RT-06 lên một nhánh mới. Không commit vào main.
Nếu nhóm yêu cầu tách PR theo issue, dùng phân nhóm bên dưới và chạy lại kiểm thử từng snapshot.

Phân nhóm review/split:
- RT-01: Hub/group và phần đăng ký route có dependency recovery.
- RT-02: xác nhận interface có sẵn + tài liệu JSON/SessionCompleted, không tạo code trùng.
- RT-03: publisher + DI transport.
- RT-04: timer manager/service/worker, BeginAnswering và thay đổi orchestrator liên quan deadline.
- RT-05: snapshot, caller recovery/replay, tài liệu frontend.
- RT-06: csproj packages và bộ test; chạy test cho từng snapshot PR sau khi chia.
Các file Hub/service/DI có phần giao nhau; không stage cả file tùy tiện khi chia issue,
và không tạo PR thiếu dependency làm build hỏng.

Review `git status`, `git diff`, `git diff --check` trước staging.
Stage file task rõ ràng; bỏ qua bin/obj/TestResults và config chứa secret.
Commit title/PR body ghi đúng issue thực tế, shared files sửa, 125 tests đạt với SQL tests excluded,
fake boundaries, flag enable và giới hạn single-process timer.
Không mô tả PR là toàn bộ hệ thống đã tích hợp production khi các điểm nối trên chưa được kiểm chứng.
