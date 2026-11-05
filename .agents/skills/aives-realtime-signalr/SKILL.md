---
name: aives-realtime-signalr
description: Enforce SignalR Hub design, authorized group management, and realtime event contracts for AIVES Viva Examination & AI Grading.
---

# AIVES Realtime SignalR Governance

This skill governs the development and review of realtime components in **AIVES (Group 3: AI Interview Core and Group 4: AI-Assisted Viva Exam Grading)**.

## 1. Hub Architecture & Placement

* **Location:** `StudentNameMVC/Hubs/VivaHub.cs`
* **Route:** Map to `/hubs/viva` in `Program.cs`.
* **Transport:** WebSockets preferred, falling back to Server-Sent Events / Long Polling.
* **Base Class:** Inherit from `Microsoft.AspNetCore.SignalR.Hub`.

---

## 2. Server-Side Group Authorization & Security

1. **Group Isolation:**
   - **Student Examination Session Group:**
     `viva-session-{sessionId:D}`
     - Restricted to the student assigned to the session and authorized exam supervisors.
   - **Lecturer Grading Review Group (Group 4):**
     `grading-{examId:D}`
     - Restricted to authenticated users with `Lecturer` or `Admin` roles assigned to that exam.
     - Clients must **NEVER** broadcast to `Clients.All` or `Clients.Others`. All viva events are scoped strictly to `Clients.Group(groupName)`.
2. **Authorization on Join:**
   - When joining an examination session (`JoinSession(Guid sessionId)`):
     - Check `ClaimsPrincipal` via `Context.User`.
     - Invoke BLL authorization check. If unauthorized, throw `HubException`.
   - When joining a grading review channel (`JoinGradingGroup(Guid examId)`):
     - Verify caller has `Lecturer` or `Admin` role claim.
     - Add connection to `grading-{examId:D}`.
3. **No Sensitive Data Leaks:**
   - Never broadcast AI system prompts, raw API keys, internal exception traces, or cross-student confidential evaluations.

---

## 3. Approved Realtime Events & Payloads

Teammates must strictly use the approved event names defined in [`docs/G4-CONTRACTS.md`](../../docs/G4-CONTRACTS.md) and [`docs/ASM2-CONTRACTS.md`](../../docs/ASM2-CONTRACTS.md):

### Group 3 (AI Interview Core) Events
| Event Name | Trigger Condition | Primary Payload |
| :--- | :--- | :--- |
| `SessionStatusChanged` | State machine transitions (Ready, Delivered, etc.) | `VivaSessionStatusDto` |
| `TranscriptReady` | Audio transcribed and persisted to DB | `VivaTranscriptDto` |
| `FollowUpGenerated` | LLM synthesized follow-up question and audio ready | `VivaFollowUpDto` |
| `TimeRemainingChanged` | Countdown updates during answering | `VivaTimeRemainingDto` |
| `SessionCompleted` | All turns finished, session finalized | `SessionCompletedDto` |
| `VivaError` | Operation failure (STT, LLM, or network error) | `VivaErrorDto` |

### Group 4 (AI Grading & HITL) Events
| Event Name | Trigger Condition | Primary Payload |
| :--- | :--- | :--- |
| `GradingStarted` | Background worker begins processing session | `GradingStartedDto` |
| `GradingCompleted` | AI grading finished and persisted to DB | `GradingCompletedDto` |
| `GradingFailed` | AI and fallback grading failed | `GradingFailedDto` |

---

## 4. "Persistence-First" Publishing Rule

* **Authoritative Database Rule:**
  - Realtime notifications are a **projection of persisted state**, not the source of truth.
  - The BLL Orchestrator must **ALWAYS commit changes to SQL Server via `SaveChangesAsync` BEFORE calling `IVivaRealtimePublisher`**.
  - For Group 4: `VivaGradingResult` records and the updated `VivaSession.Status` must be successfully committed to SQL Server before broadcasting `GradingCompleted`.
  - If database persistence fails or throws a concurrency exception, the event **MUST NOT** be broadcast to the client.

---

## 5. Client Integration & Reconnection

1. **Lecturer Grading Page (`/Grading/Index` & `/Grading/Review`):**
   - SignalR client connects to `/hubs/viva` upon page load.
   - Invokes `JoinGradingGroup(examId)`.
   - Handles `GradingStarted` by updating session badge to `"Đang chấm điểm..."` (animated spinner).
   - Handles `GradingCompleted` by updating status badge to `"Chờ duyệt"`, displaying tentative AI score, and enabling the review link without requiring manual browser refresh (F5).
   - Handles `GradingFailed` by updating status badge to `"Lỗi chấm điểm"` with a retry button.
2. **Reconnection & Resilience:**
   - Client scripts must configure `.withAutomaticReconnect([0, 2000, 5000, 10000])`.
   - On reconnected event, rejoin the grading room to resume receiving events.
   - Hub methods and publishers must accept and respect `CancellationToken` (e.g. `Context.ConnectionAborted`).

---

## 6. Integration Testing Guidelines

* Write integration tests using `Microsoft.AspNetCore.SignalR.Client.HubConnectionBuilder` against a `WebApplicationFactory<Program>` test server.
* Verify:
  - Non-lecturer users (e.g. `Student` or unauthenticated) are rejected when joining `JoinGradingGroup`.
  - Background grading worker triggers `GradingStarted` and `GradingCompleted` in order with valid payloads.
  - Persistence-first is respected (records exist in database when event arrives).
