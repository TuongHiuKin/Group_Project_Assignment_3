---
name: aives-architecture
description: Enforce AIVES 3-layer architecture, Razor Pages, MVC, BLL, and DAL boundaries for Group 4 AI Grading & Group 3 Interview Core.
---

# AIVES Architecture — Core Governance

This skill defines the official architecture rules for **AIVES — AI-powered Viva Examination System** (Group 4: AI-Assisted Viva Exam Grading with HITL, Group 3: AI Interview Core, and Group 6: Reporting).

## 1. Project Identity & Architecture Layers

The system follows a strict **3-Layer Architecture** implemented across .NET 8 projects:

```text
Presentation Layer: StudentNameMVC (Razor Pages, MVC Controllers, ViewModels, SignalR Hubs)
       │
       ▼ (Calls Service Interfaces)
Business Logic Layer: AIVES.BLL (Services, Use Cases, Business Rules, Orchestration, AI Abstractions, Background Workers)
       │
       ▼ (Calls Repository Interfaces)
Data Access Layer: AIVES.DAL (Repositories, Entities, EF Core AIVESDbContext)
       │
       ▼ (Executes T-SQL via EF Core / LINQ)
Database: Microsoft SQL Server
```

### Official Request Paths
1. **Interactive Viva Exam Flow (Group 3):**
   ```text
   Razor Page (.cshtml) / PageModel (.cshtml.cs)
       → BLL Service (IVivaSessionOrchestrator)
           → DAL Repository (IVivaSessionRepository, etc.)
               → AIVESDbContext (Scoped)
                   → SQL Server
   ```
2. **Background Grading Flow (Group 4):**
   ```text
   Channel Queue (System.Threading.Channels)
       → VivaGradingBackgroundService (BackgroundService)
           → Creates IServiceScope
               → IGradingOrchestrator (BLL)
                   → IAnswerGradingService (AI Adapters: Gemini / OpenAI / Mock)
                   → IVivaGradingRepository (DAL)
                       → AIVESDbContext (Scoped) → SQL Server
                   → IVivaRealtimePublisher (Realtime)
                       → VivaHub (SignalR) → Lecturer Group
   ```
3. **Teacher Grading Review & Override Flow (Group 4):**
   ```text
   Razor Page (/Grading/Index, /Grading/Review/{sessionId})
       → BLL Service (IGradingOrchestrator)
           → DAL Repository (IVivaGradingRepository, IVivaRubricRepository)
               → AIVESDbContext (Scoped)
                   → SQL Server
   ```
4. **Realtime SignalR Flow:**
   ```text
   Browser Client
       → SignalR Hub (VivaHub)
           → BLL Service / IVivaRealtimePublisher
               → DAL Repository
                   → AIVESDbContext (Scoped)
                       → SQL Server
   ```
5. **Reporting & Historical Flow (Group 6 & Group 4):**
   ```text
   Razor View (.cshtml) / Controller (ExamReportsController)
       → BLL Service (IExamReportRepository / Report Services)
           → DAL Repository (Reads persisted VivaGradingResult, VivaSession)
               → AIVESDbContext (Scoped)
                   → SQL Server
   ```

---

## 2. Layer Responsibilities & Boundaries

### 2.1 Presentation Layer (`StudentNameMVC`)
* **Responsibilities:**
  - HTTP requests, Razor Page models, MVC Controllers, ViewModels.
  - Razor Pages for examination (`/Pages/Viva/Exam.cshtml`) and grading review (`/Pages/Grading/Index.cshtml`, `/Pages/Grading/Review.cshtml`).
  - SignalR Hub transport (`VivaHub`), client connection management.
  - Authentication cookies, role-based authorization (`Lecturer`, `Student`, `Staff`), and UI-level ModelState validation.
* **Strict Constraints:**
  - **NEVER** inject `AIVESDbContext` or `DbSet<T>` into any Controller, Razor PageModel, or SignalR Hub.
  - **NEVER** inject a DAL Repository directly into Controllers, PageModels, or Hubs.
  - **NEVER** execute raw SQL or LINQ queries against the database in Presentation.
  - Always communicate through **BLL Service interfaces** (`IVivaSessionOrchestrator`, `IGradingOrchestrator`).

### 2.2 Business Logic Layer (`AIVES.BLL`)
* **Responsibilities:**
  - Examination state machines, follow-up count limits, answer transcription orchestration.
  - AI grading orchestration (`IGradingOrchestrator`), isolated question grading, fallback to `RubricGradingService` on failure.
  - Background task scheduling and queue management via `System.Threading.Channels`.
  - Concurrency checks and audit trail generation (`VivaGradeChangeLog`) when lecturers finalize or override scores.
  - Security authorization checks (e.g. verifying that student owns the exam attempt, lecturer is assigned).
* **Strict Constraints:**
  - **NEVER** reference EF Core `AIVESDbContext` or SQL Server in BLL.
  - **NEVER** reference HTTP-specific types (`HttpContext`, `HttpRequest`, `ViewData`, `HubCallerContext`) in BLL.
  - Services must depend only on **Repository interfaces** (`AIVES.DAL.Repositories.Interfaces`) and **AI service interfaces** (`AIVES.BLL.Interfaces.Ai`).

### 2.3 Data Access Layer (`AIVES.DAL`)
* **Responsibilities:**
  - EF Core entity mapping and configurations (`VivaGradingResultConfiguration`, `VivaGradeChangeLogConfiguration`, `VivaRubricCriterionConfiguration`).
  - LINQ queries (`.Where()`, `.OrderBy()`, `.Include()`, `.AsNoTracking()`).
  - Unit of persistence (`SaveChangesAsync`) with concurrency control (`RowVersion`).
  - Database schema scripts (`005-create-viva-grading.sql`, `006-seed-viva-rubric.sql`).
* **Strict Constraints:**
  - Repositories directly use scoped `AIVESDbContext`.
  - Repositories must **NEVER** contain business policy decisions (e.g., fallback logic belongs in BLL).
  - Repositories and `AIVESDbContext` must **ALWAYS** be registered as **Scoped**. **NEVER** register a Repository as Singleton.

---

## 3. Razor Pages & SignalR Conventions

1. **Razor Pages Folder Structure:**
   - Viva Exam: `StudentNameMVC/Pages/Viva/` (`Exam.cshtml`, `Exam.cshtml.cs`).
   - Viva Grading: `StudentNameMVC/Pages/Grading/`:
     - `Index.cshtml` & `Index.cshtml.cs`: List sessions by exam, filter by grading state (`Pending`, `AiGraded`, `Finalized`, `Failed`). Accessible only to `Lecturer` / `Admin`.
     - `Review.cshtml` & `Review.cshtml.cs`: Detailed session transcript, question-by-question AI evaluation, teacher score override and confirmation form.
2. **SignalR Hub Conventions:**
   - Hubs live in `StudentNameMVC/Hubs/VivaHub.cs`.
   - SignalR endpoint: `/hubs/viva`.
   - Groups:
     - Student viva room: `viva-session-{sessionId}`.
     - Lecturer grading room: `grading-{examId}`.
3. **Approved SignalR Event Names:**
   - Group 3: `SessionStatusChanged`, `TranscriptReady`, `FollowUpGenerated`, `TimeRemainingChanged`, `SessionCompleted`, `VivaError`.
   - Group 4: `GradingStarted`, `GradingCompleted`, `GradingFailed`.

---

## 4. Architecture Drift Prevention

* Do not introduce Minimal APIs, CQRS/MediatR, Clean Architecture Clean-Domain splits, or external microservices.
* Do not introduce foreign JavaScript frontend frameworks (React, Angular, Vue); use vanilla modern JavaScript or lightweight modules in Razor Pages.
* Preserve existing Group 6 Reporting and Group 3 Interview modules without regression.
* All new database tables must integrate with `AIVESDbContext` via EF Core Fluent API.

---

## 5. Testing Expectations

* **Unit Tests (`AIVES.Tests`):**
  - Test BLL state machine transitions and limits using Mock repositories and Mock AI services.
  - Test `GradingOrchestrator` both on successful AI grading and fallback keyword-based evaluation.
  - Test Repository query and persistence logic against in-memory or integration fixtures.
* **Realtime & Queue Tests:**
  - Verify SignalR group authorization and event payloads without requiring active browser audio hardware.
  - Verify background worker queue drains jobs and handles transient errors gracefully.
