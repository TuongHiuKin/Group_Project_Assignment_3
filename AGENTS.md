# AIVES — Agent & Team Governance Instructions

## 1. Project Identity & Assignment 2 Scope

**AIVES — AI-powered Viva Examination System** is a 3-layer enterprise ASP.NET Core .NET 8 application.

* **Current Active Goal (Assignment 2):** **Group 3 — AI Interview Core**.
  - Interactive viva examination flow in **Razor Pages** (`/Viva/Exam/{sessionId}`).
  - Question presentation & **Text-to-Speech (TTS)** audio playback.
  - Student voice recording via microphone & **Speech-to-Text (STT)** transcription.
  - Realtime event broadcasting via **SignalR** (`VivaHub`).
  - Contextual **AI Follow-up Question** generation (LLM) & limits enforcement.
  - Turn transcript and follow-up persistence in **SQL Server via EF Core Repositories**.
* **Pre-existing Modules:** Group 6 (Feedback & Reports) is already implemented and must be preserved. Legacy News Management remains in source code history only; it is not a criteria for Assignment 2.
* **Out of Scope for ASM2:** Group 4 (AI Scoring & Grading) is strictly reserved for a future assignment. Do NOT implement automated scoring in Assignment 2.

---

## 2. Agent Skill Routing

Every agent working on this repository must read and follow the designated skill for their assigned domain:

* **Architecture, Boundaries & Razor Pages:** [`.agents/skills/aives-architecture/SKILL.md`](.agents/skills/aives-architecture/SKILL.md)
* **Realtime SignalR Hubs & Events:** [`.agents/skills/aives-realtime-signalr/SKILL.md`](.agents/skills/aives-realtime-signalr/SKILL.md)
* **AI Integrations (STT, TTS, LLM):** [`.agents/skills/aives-ai-integration/SKILL.md`](.agents/skills/aives-ai-integration/SKILL.md)
* **Git Tasks, Branching & PRs:** [`.agents/skills/team-git-workflow/SKILL.md`](.agents/skills/team-git-workflow/SKILL.md)
* *(Note: `.agents/skills/funews-architecture/SKILL.md` is deprecated and superseded by `aives-architecture`).*

---

## 3. Approved Authoritative Documents

Before implementing code or creating tasks, agents must consult:

1. **Shared Contracts & Events:** [`docs/ASM2-CONTRACTS.md`](docs/ASM2-CONTRACTS.md) (The single source of truth for DTOs, SignalR events, Hub routes, and service interfaces).
2. **Team Task Board (29 Issues):** [`docs/ASM2-TASK-BOARD.md`](docs/ASM2-TASK-BOARD.md) (Task definitions, dependencies, and assigned roles).
3. **Architecture Overview:** [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).
4. **Code Review Checklist:** [`docs/CODE-REVIEW-CHECKLIST.md`](docs/CODE-REVIEW-CHECKLIST.md).

---

## 4. Architecture Boundaries & Constraints

* **3-Layer Physical Architecture:**
  - **Presentation Layer:** `StudentNameMVC` (hosts Razor Pages, MVC Controllers, SignalR Hubs, ViewModels).
  - **Business Logic Layer:** `AIVES.BLL` (Services, Orchestration, Business Rules, AI Abstractions).
  - **Data Access Layer:** `AIVES.DAL` (Entities, Scoped Repositories, Scoped `AIVESDbContext`).
* **Request Flow:**
  - `Razor Page / MVC Controller / SignalR Hub → BLL Service → DAL Repository → AIVESDbContext → SQL Server`.
* **Zero Direct Database Access:**
  - Razor Pages, Controllers, and SignalR Hubs must **NEVER** inject `AIVESDbContext` or Repositories directly.
  - BLL Services must **NEVER** reference `AIVESDbContext` or EF Core.
* **Scoped Lifetimes:**
  - Repositories and `AIVESDbContext` are always **Scoped**. Never register Repositories as Singleton.
  - DAO pass-through layer has been consolidated into Repositories; do not recreate separate DAO classes.
* **Persistence First for Realtime:**
  - Authoritative state must be persisted to SQL Server via `SaveChangesAsync` before broadcasting SignalR events.

---

## 5. Team Roles & Shared-File Governance

The team consists of 5 members:
1. **Tech Lead / Reviewer (Member 1):** Owns architectural boundaries, shared contracts (`ASM2-CONTRACTS.md`), BLL orchestration (`IVivaSessionOrchestrator`), dependency injection, and PR reviews.
2. **Razor Pages Frontend (Member 2):** Owns UI (`/Viva/Exam`), WebAudio recording, client SignalR JS.
3. **SignalR Realtime (Member 3):** Owns `VivaHub`, session group security, and `IVivaRealtimePublisher`.
4. **Database & Repositories (Member 4):** Owns Viva domain entities, EF Core configurations, and Repositories.
5. **AI Integration (Member 5):** Owns STT, TTS, and LLM follow-up service adapters and mocks.

**Shared-File Rule:**
Only the Tech Lead is authorized to modify or approve modifications to:
- `Program.cs` (application-wide DI and middleware).
- `AIVESDbContext.cs` and core database migration scripts.
- Shared interface contracts in `AIVES.BLL.Interfaces` and `AIVES.DAL.Repositories.Interfaces`.
- SignalR event names and payload definitions.

---

## 6. Task Scope & Git Safety Rules

* **Task-Scoped Work:** Agents must implement only the assigned issue. Never create unrelated features or rewrite working modules.
* **Branch Convention:** `<member-name>/<feature-name>` (e.g. `kien/asm2-lead-04-orchestrator`, `member2/asm2-ui-03-microphone`).
* **1-Issue-1-Branch-1-PR Rule:** Each issue is isolated on its own feature branch and submitted via a Pull Request with the issue ID clearly referenced.
* **Explicit User Authorization Required:**
  - Never commit, push, create a PR, or merge automatically.
  - Never run destructive git commands (`git reset --hard`, `git clean -fd`, `git checkout -- .`, force-push).
  - Unit tests (`dotnet test`) must pass with 0 errors before submitting any PR.
