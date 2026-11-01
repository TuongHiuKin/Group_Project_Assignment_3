# G4 Background Grading & SignalR — Handoff (RT-01 → RT-06)

Implements issues #19–#24 of `docs/G4-TASK-BOARD.md` against `docs/G4-CONTRACTS.md`.

## What runs where

```
Student completes viva
  └─ VivaSessionOrchestrator.CompleteSessionAsync
       ├─ SaveChanges (Completed)                         persistence first
       └─ IGradingQueue.TryEnqueue(GradingJob)            non-blocking (RT-01)

VivaGradingBackgroundService (single reader)             AIVES.BLL/Services/Grading
  ├─ startup: IGradingRecoveryScanner → re-queue unfinished sessions (RT-04)
  └─ per job → GradingJobProcessor, own DI scope
       per question:
         GradingStarted ─► IGradingOrchestrator.Grade/RegradeQuestionAsync (persists)
                         ─► GradingCompleted | GradingFailed            (RT-02)
         transient errors: retry 3× (2s/4s/8s + jitter)                  (RT-04)

Lecturer page ─ grading-realtime.js ─ /hubs/viva ─ JoinGradingGroup(examId) → group grading-{EXAMID}
                                                 └ RequestRegrade(sessionId, questionId?)  (RT-03)
```

## Contract decisions taken (to confirm with the Tech Lead)

| Topic | Decision |
| :--- | :--- |
| Group name | `grading-{examId}` with the string exam ID (e.g. `grading-EXAM01`), upper-cased; IDs must match `[A-Za-z0-9_-]{1,64}`. |
| `GradingStarted` granularity | Per question (`SessionId`, `QuestionId`), as in the contract. |
| Who may join | `Lecturer` or `Admin` role. No per-exam lecturer assignment exists in the schema yet. |
| Badge labels | `docs/G4-CONTRACTS.md` §7. |
| Publisher location | Interface in BLL (`IVivaRealtimePublisher`), implementation stays in `StudentNameMVC/Services` (needs `IHubContext`). |
| `IGradingOrchestrator`, `GradingState`, grading DTOs | Copied verbatim from the contract into `AIVES.BLL.Interfaces.Grading` / `AIVES.BLL.Models.Grading` so this work compiles before LEAD-03. Lead's version wins on merge. |

## Behaviour guarantees

- **No duplicate grading (RT-03):** a job is "in flight" from enqueue until processed. The same question (or a whole session) cannot be queued twice; the single reader never grades two jobs at once.
- **Never overwrite a finalized score (RT-03):** the worker re-reads state before each question and skips `Finalized`. A `GradingConflictException` (or EF `DbUpdateConcurrencyException`) during upsert is treated as "lecturer won": nothing is overwritten or broadcast.
- **Idempotent initial grading:** `GradingJobKind.Initial` only grades questions without a result (or `Pending`). `Regrade` re-grades everything that is not `Finalized`.
- **Recovery (RT-04):** on start, completed sessions with no grading rows, a `Pending` row, or an answered question without a row are re-queued. `Failed` rows are not auto-retried (no endless loops); they wait for "Chấm lại bằng AI".
- **Safe before LEAD-03:** if `IGradingOrchestrator` is not registered, jobs are skipped with a warning and sessions stay pending for recovery. Nothing else changes for the running app.
- **No secrets on the wire:** `GradingFailed.ErrorMessage` is always a fixed Vietnamese message.

## What other members need to do

**Tech Lead (LEAD-03 / LEAD-05)**
- Register `GradingOrchestrator` as `IGradingOrchestrator` (Scoped). The worker is already registered by `AddVivaRealtime()` → `AddVivaGradingWorker()`.
- `GradeQuestionAsync` / `RegradeQuestionAsync` must return only after `SaveChangesAsync`, and should persist `Failed` when both AI and keyword fallback fail.
- `RegradeQuestionAsync` should refuse to overwrite `Finalized` (defence in depth) and translate `DbUpdateConcurrencyException` to `GradingConflictException`.
- `GetSessionGradingAsync` should return `null` (or an empty list) when the session has no rows yet.

**Database (DB-03)**
- `UpsertAiResultAsync` must not overwrite a `Finalized` row and must use `RowVersion`.

**Frontend (UI-01 / UI-02 / UI-04)**: add the markup below and the two scripts.

```html
<div data-grading-realtime data-exam-id="@Model.ExamId"
     data-status-url="@Url.Page("/Grading/Index", "Status", new { examId = Model.ExamId })">
  <small data-grading-connection class="text-muted"></small>
  <div data-grading-message></div>

  <!-- Index: one row per session -->
  <tr data-grading-session="@s.SessionId">
    <td><span data-grading-badge class="badge bg-secondary">Chờ chấm điểm</span></td>
  </tr>

  <!-- Review: one block per question -->
  <div data-grading-session="@s.SessionId" data-grading-question="@q.QuestionId">
    <span data-grading-badge></span> <span data-grading-score></span>
    <button type="button" class="btn btn-sm btn-outline-primary" data-grading-regrade
            data-session-id="@s.SessionId" data-question-id="@q.QuestionId">Chấm lại bằng AI</button>
  </div>
</div>

@section Scripts {
  <script src="https://cdnjs.cloudflare.com/ajax/libs/microsoft-signalr/8.0.7/signalr.min.js"></script>
  <script src="~/js/grading-realtime.js" asp-append-version="true"></script>
}
```

`data-status-url` is optional. When present, it is called after every (re)connect to catch up on missed events and must return
`[{ "sessionId": "...", "questionId": "Q1", "state": 1, "earnedScore": 7.5, "maxScore": 10 }]`.
The script also raises a `grading:update` DOM event (`detail: { sessionId, questionId, state, score }`) for custom UI.

## Events (server → client)

| Event | Payload (camelCase JSON) |
| :--- | :--- |
| `GradingStarted` | `{ sessionId, questionId, startedAtUtc }` |
| `GradingCompleted` | `{ sessionId, questionId, state (0–3), earnedScore, maxScore, isAiGraded }` |
| `GradingFailed` | `{ sessionId, questionId, errorMessage, fallbackTriggered }` |

Hub methods: `JoinGradingGroup(examId)`, `LeaveGradingGroup(examId)`, `RequestRegrade(sessionId, questionId | null)`. `RequestRegrade` returns
`Queued | AlreadyInProgress | AlreadyFinalized | SessionNotFound | SessionNotCompleted | QueueUnavailable`.

## Configuration (`appsettings.json`, optional)

```json
"Grading": { "Worker": { "QueueCapacity": 100, "MaxRetries": 3, "RetryBaseDelay": "00:00:02", "RecoverOnStartup": true } }
```

## Tests (RT-06)

- `AIVES.Tests/Realtime/GradingQueueTests.cs`: FIFO, bounded capacity, duplicate guard.
- `AIVES.Tests/Realtime/GradingWorkerTests.cs`: event order, persistence-first, per-question isolation, retry/backoff, cancellation, regrade guards, recovery, single-reader concurrency, enqueue on completion.
- `AIVES.Tests/Realtime/GradingSignalRTests.cs`: non-lecturers rejected; payloads match the contract; exam isolation; leave group; full hub → queue → worker → SignalR regrade flow.
