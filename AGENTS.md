# AIVES — Agent & Team Governance Instructions

## 1. Project Identity & Scope

**AIVES — AI-powered Viva Examination System** is a 3-layer enterprise ASP.NET Core .NET 8 application.

* **Current Active Goal (Assignment 3 / Group Project):** **Group 4 — AI-Assisted Viva Exam Grading with Human-in-the-Loop (HITL)**.
  - Asynchronous question-by-question grading via background queue (`System.Threading.Channels` + `VivaGradingBackgroundService`).
  - Multi-provider AI grading adapters (`GeminiAnswerGradingService`, `OpenAiAnswerGradingService`, `MockAnswerGradingService`) implementing `IAnswerGradingService`.
  - Database-driven rubric criteria seeded per question (`VivaRubricCriterion`), replacing hard-coded `RubricCatalog`.
  - Fault isolation per question with automatic fallback to keyword-based `RubricGradingService` if AI calls fail or time out.
  - Realtime grading notifications to lecturer groups via **SignalR** (`VivaHub` events: `GradingStarted`, `GradingCompleted`, `GradingFailed`).
  - Human-in-the-Loop review and override workflow in **Razor Pages** (`/Grading/Index`, `/Grading/Review/{sessionId}`).
  - Concurrency-safe score confirmation ("Xác nhận điểm AI") or manual override ("Nhập điểm GV") with mandatory audit trail logging (`VivaGradeChangeLogs`).
  - Immediate student visibility of tentative scores marked **"AI chấm, chờ giảng viên duyệt"**, updated to **"Điểm chính thức"** once confirmed by the lecturer.
* **Pre-existing Modules:**
  - **Group 3 (AI Interview Core):** Interactive viva room (`/Viva/Exam/{sessionId}`), WebAudio recording, STT/TTS, follow-up questions, SignalR session management. Fully implemented and preserved.
  - **Group 6 (Feedback & Reports):** Student and class reports, rubric keyword evaluation fallback. Preserved and updated to read persisted `VivaGradingResult`.
  - Legacy News Management remains in source code history only; it is not a criteria for this project.

---

## 2. Agent Skill Routing

Every agent working on this repository must read and follow the designated skill for their assigned domain:

* **Architecture, Boundaries & Razor Pages:** [`.agents/skills/aives-architecture/SKILL.md`](.agents/skills/aives-architecture/SKILL.md)
* **Realtime SignalR Hubs & Background Queue:** [`.agents/skills/aives-realtime-signalr/SKILL.md`](.agents/skills/aives-realtime-signalr/SKILL.md)
* **AI Integrations (STT, TTS, LLM Follow-Up, LLM Grading):** [`.agents/skills/aives-ai-integration/SKILL.md`](.agents/skills/aives-ai-integration/SKILL.md)
* **Git Tasks, Branching & PRs:** [`.agents/skills/team-git-workflow/SKILL.md`](.agents/skills/team-git-workflow/SKILL.md)
* *(Note: `.agents/skills/funews-architecture/SKILL.md` is deprecated and superseded by `aives-architecture`).*

---

## 3. Approved Authoritative Documents

Before implementing code or creating tasks, agents must consult:

1. **Shared Contracts & Events (Group 4):** [`docs/G4-CONTRACTS.md`](docs/G4-CONTRACTS.md) (The single source of truth for Group 4 DTOs, grading states, SignalR events, and service interfaces).
2. **Team Task Board (Group 4 - 30 Issues):** [`docs/G4-TASK-BOARD.md`](docs/G4-TASK-BOARD.md) (Task definitions, dependencies, and assigned roles).
3. **Group 3 Historical Contracts:** [`docs/ASM2-CONTRACTS.md`](docs/ASM2-CONTRACTS.md) and [`docs/ASM2-TASK-BOARD.md`](docs/ASM2-TASK-BOARD.md).
4. **Architecture Overview:** [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).
5. **Code Review Checklist:** [`docs/CODE-REVIEW-CHECKLIST.md`](docs/CODE-REVIEW-CHECKLIST.md).

---

## 4. Architecture Boundaries & Constraints

* **3-Layer Physical Architecture:**
  - **Presentation Layer:** `StudentNameMVC` (hosts Razor Pages `/Grading`, `/Viva`, MVC Controllers, SignalR Hubs, ViewModels).
  - **Business Logic Layer:** `AIVES.BLL` (Services, Orchestration, Business Rules, AI Abstractions, Background Services).
  - **Data Access Layer:** `AIVES.DAL` (Entities, Scoped Repositories, Scoped `AIVESDbContext`).
* **Request & Execution Flows:**
  - **Razor Page / MVC Controller Flow:**
    `Razor Page / Controller → BLL Service (IGradingOrchestrator) → DAL Repository → AIVESDbContext → SQL Server`.
  - **Background Grading Execution Flow:**
    `Channel Queue → VivaGradingBackgroundService (IServiceScopeFactory) → IGradingOrchestrator → IVivaGradingRepository / IAnswerGradingService → DbContext → SQL Server → IVivaRealtimePublisher → VivaHub`.
* **Zero Direct Database Access:**
  - Razor Pages, Controllers, Background Services, and SignalR Hubs must **NEVER** inject `AIVESDbContext` or Repositories directly.
  - BLL Services must **NEVER** reference `AIVESDbContext` or EF Core.
* **Scoped Lifetimes:**
  - Repositories and `AIVESDbContext` are always **Scoped**. Never register Repositories as Singleton.
  - Background workers must create a new `IServiceScope` for each grading job to safely resolve scoped services (`IGradingOrchestrator`, repositories).
* **Persistence First for Realtime:**
  - Authoritative state must be persisted to SQL Server via `SaveChangesAsync` before broadcasting SignalR events (`GradingStarted`, `GradingCompleted`, `GradingFailed`).
* **Locked Business Decisions for Group 4:**
  1. Students see AI score immediately after exam completion with label `"AI chấm, chờ giảng viên duyệt"`.
  2. Teacher review: "Xác nhận điểm AI" -> final score = AI score; or teacher enters custom score -> final score = teacher score. Label becomes `"Điểm chính thức"`.
  3. Editing a finalized score requires mandatory `Reason` and records an audit entry in `VivaGradeChangeLogs`.
  4. Rubrics for exam questions are stored in and read from SQL Server (`VivaRubricCriteria`), replacing hardcoded `RubricCatalog`.
  5. AI grades each question independently. AI failure falls back to keyword-based `RubricGradingService` without failing other questions.

---

## 5. Team Roles & Shared-File Governance

The team consists of 5 members:
1. **Tech Lead / Reviewer (Member 1 - Kien):** Owns architectural boundaries, shared contracts (`G4-CONTRACTS.md`), BLL orchestration (`IGradingOrchestrator`), DI registration in `Program.cs`, reporting integration (`ExamReportRepository`), and PR reviews.
2. **Database & Repositories (Member 2):** Owns `VivaGradingResult`, `VivaGradeChangeLog`, `VivaRubricCriterion` entities, EF Core configurations, `IVivaGradingRepository`, `IVivaRubricRepository`, SQL migration scripts `005` & `006`, and seed data extensions.
3. **AI Integration (Member 3):** Owns `IAnswerGradingService`, `MockAnswerGradingService`, structured prompt design in `AiPrompts`, `GeminiAnswerGradingService`, `OpenAiAnswerGradingService`, output validation/injection guardrails, and speech metrics.
4. **Background & Realtime (Member 4):** Owns `System.Threading.Channels` queue, `VivaGradingBackgroundService`, startup recovery of pending jobs, SignalR lecturer group authorization, realtime grading events, regrade triggers, and client JS updates.
5. **Razor Pages Frontend (Member 5):** Owns lecturer grading UI (`/Grading/Index`, `/Grading/Review/{sessionId}`), score review/override forms, regrade button, audit log display, student/class report badge updates, and UI automated tests.

**Shared-File Rule:**
Only the Tech Lead is authorized to modify or approve modifications to:
- `Program.cs` (application-wide DI and middleware).
- `AIVESDbContext.cs` and core database migration scripts.
- Shared interface contracts in `AIVES.BLL.Interfaces` and `AIVES.DAL.Repositories.Interfaces`.
- SignalR event names and payload definitions.

---

## 6. Task Scope & Git Safety Rules

* **Task-Scoped Work:** Agents must implement only the assigned issue. Never create unrelated features or rewrite working modules.
* **Branch Convention:** `<member-name>/g4-<feature-name>` (e.g. `kien/g4-lead-03-orchestrator`, `member2/g4-db-01-entities`).
* **1-Issue-1-Branch-1-PR Rule:** Each issue is isolated on its own feature branch and submitted via a Pull Request with the issue ID clearly referenced.
* **Explicit User Authorization Required:**
  - Never commit, push, create a PR, or merge automatically.
  - Never run destructive git commands (`git reset --hard`, `git clean -fd`, `git checkout -- .`, force-push).
  - Unit tests (`dotnet test`) must pass with 0 errors before submitting any PR.

---

## 7. Build, Test & Run Commands

* **Build Solution:**
  `dotnet build AIVESSystem.sln`
* **Run All Tests:**
  `dotnet test AIVESSystem.sln`
* **Run Unit/Integration Test Project:**
  `dotnet test AIVES.Tests/AIVES.Tests.csproj`
* **Run E2E Test Project:**
  `dotnet test AIVES.E2E/AIVES.E2E.csproj`
* **Run Screenshot Tests:**
  `dotnet test AIVES.E2E/AIVES.E2E.csproj --filter Category=Screenshot`
* **Run Web Application:**
  `dotnet run --project StudentNameMVC/StudentNameMVC.csproj`

* **Test Folders:**
  - `AIVES.Tests/` (Unit, Integration, Architecture, Realtime, Razor Pages tests)
  - `AIVES.E2E/` (Playwright E2E browser tests & Screenshot capture suite)

* **Local Directory Resolution:**
  `LOCAL_DIR = <main repo>/.local`, resolved via git common dir (works from worktrees):
  Parent of `git rev-parse --path-format=absolute --git-common-dir` joined with `.local`.

* **Screens (Route List):**
  - `/` (Home page)
  - `/Account/Login` (Authentication)
  - `/Viva/Exam/{sessionId}` (Interactive viva interview room)
  - `/Grading/Index` (Lecturer grading overview & session queue)
  - `/Grading/Review/{sessionId}` (Lecturer score review, audit logs & override)
  - `/Reports/Student` (Student viva exam score & rubric feedback report)
  - `/Reports/Class` (Class-level grading aggregation report)
