---
name: aives-realtime-signalr
description: Enforce SignalR Hub design, authorized group management, and realtime event contracts for AIVES Viva Examination.
---

# AIVES Realtime SignalR Governance

This skill governs the development and review of realtime components in **AIVES Assignment 2 (Group 3: AI Interview Core)**.

## 1. Hub Architecture & Placement

* **Location:** `StudentNameMVC/Hubs/VivaHub.cs`
* **Route:** Map to `/hubs/viva` in `Program.cs`.
* **Transport:** WebSockets preferred, falling back to Server-Sent Events / Long Polling.
* **Base Class:** Inherit from `Microsoft.AspNetCore.SignalR.Hub` or strongly-typed `Hub<IVivaClientEvents>`.

---

## 2. Server-Side Group Authorization & Security

1. **Strict Group Boundary:**
   - Every viva examination session belongs to an isolated SignalR group:
     `viva-session-{sessionId:D}`
   - Clients must **NEVER** broadcast to `Clients.All` or `Clients.Others`. All viva events are scoped strictly to `Clients.Group(groupName)`.
2. **Authorization on Join:**
   - When a client invokes `JoinSession(Guid sessionId)`:
     - The Hub retrieves the authenticated `ClaimsPrincipal` via `Context.User`.
     - The Hub invokes `IVivaSessionOrchestrator.AuthorizeUserForSessionAsync(sessionId, userId)`.
     - If unauthorized, the Hub must **REJECT** the caller immediately using `throw new HubException("Unauthorized to join this examination session.");`.
3. **No Sensitive Data Leaks:**
   - Never broadcast AI system prompt instructions, raw API keys, internal stack traces, or other students' turn data across the websocket.

---

## 3. Approved Realtime Events & Payloads

Teammates must strictly use the approved event names defined in [`docs/ASM2-CONTRACTS.md`](../../docs/ASM2-CONTRACTS.md):

| Event Name | Trigger Condition | Primary Payload |
| :--- | :--- | :--- |
| `SessionStatusChanged` | State machine transitions (Ready, QuestionDelivered, etc.) | `VivaSessionStatusDto` |
| `TranscriptReady` | Audio transcribed and persisted to DB | `VivaTranscriptDto` |
| `FollowUpGenerated` | LLM synthesized follow-up question and audio ready | `VivaFollowUpDto` |
| `TimeRemainingChanged` | Countdown updates during answering | `VivaTimeRemainingDto` |
| `SessionCompleted` | All turns finished, session finalized | `SessionCompletedDto` |
| `VivaError` | Operation failure (STT, LLM, or network error) | `VivaErrorDto` |

---

## 4. "Persistence-First" Publishing Rule

* **Authoritative Database Rule:**
  - Realtime notifications are a **projection of persisted state**, not the source of truth.
  - The BLL Orchestrator must **ALWAYS commit changes to SQL Server via `SaveChangesAsync` BEFORE calling `IVivaRealtimePublisher`**.
  - If database persistence fails or throws a concurrency exception, the event **MUST NOT** be broadcast to the client.

---

## 5. Connection & Reconnection Resilience

1. **Client Disconnects:**
   - When a student temporarily loses internet connection, the server preserves the session state in SQL Server.
   - Upon reconnecting, the student client re-invokes `JoinSession(sessionId)`.
   - The server inspects the current `VivaSessionState` from the database and immediately broadcasts the current snapshot via `SessionStatusChanged`.
2. **Heartbeats & Timeouts:**
   - Client-side countdown timers must synchronize with server-sent `TimeRemainingChanged` events to prevent client clock manipulation.
3. **Cancellation Token Propagation:**
   - Hub methods and publishers must accept and respect `CancellationToken` (e.g. `Context.ConnectionAborted`).

---

## 6. Integration Testing Guidelines

* Write integration tests using `Microsoft.AspNetCore.SignalR.Client.HubConnectionBuilder` against a `WebApplicationFactory<Program>` test server.
* Verify:
  - Unauthorized users cannot receive turn transcripts for sessions they do not own.
  - Disconnect/reconnect fetches correct current turn state.
  - Events fire with valid JSON payloads matching `docs/ASM2-CONTRACTS.md`.
