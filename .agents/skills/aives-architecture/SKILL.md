---
name: aives-architecture
description: Enforce AIVES 3-layer architecture, Razor Pages, MVC, BLL, and DAL boundaries for Assignment 2 AI Viva Examination System.
---

# AIVES Architecture — Core Governance

This skill defines the official architecture rules for **AIVES — AI-powered Viva Examination System** (Assignment 2: Group 3 AI Interview Core and existing Group 6 Reporting).

## 1. Project Identity & Architecture Layers

The system follows a strict **3-Layer Architecture** implemented across .NET 8 projects:

```text
Presentation Layer: StudentNameMVC (Razor Pages, MVC Controllers, ViewModels, SignalR Hubs)
       │
       ▼ (Calls Service Interfaces)
Business Logic Layer: AIVES.BLL (Services, Use Cases, Business Rules, Orchestration, AI Abstractions)
       │
       ▼ (Calls Repository Interfaces)
Data Access Layer: AIVES.DAL (Repositories, Entities, EF Core AIVESDbContext)
       │
       ▼ (Executes T-SQL via EF Core / LINQ)
Database: Microsoft SQL Server
```

### Official Request Paths
1. **Razor Pages Flow (Assignment 2 - Viva Exam):**
   ```text
   Razor Page (.cshtml) / PageModel (.cshtml.cs)
       → BLL Service (IVivaSessionOrchestrator)
           → DAL Repository (IVivaSessionRepository, etc.)
               → AIVESDbContext (Scoped)
                   → SQL Server
   ```
2. **Realtime SignalR Flow:**
   ```text
   Browser Client
       → SignalR Hub (VivaHub)
           → BLL Service / IVivaRealtimePublisher
               → DAL Repository
                   → AIVESDbContext (Scoped)
                       → SQL Server
   ```
3. **Existing MVC Flow (Group 6 Reporting & Account Management):**
   ```text
   Razor View (.cshtml)
       → MVC Controller
           → BLL Service
               → DAL Repository
                   → AIVESDbContext (Scoped)
                       → SQL Server
   ```

---

## 2. Layer Responsibilities & Boundaries

### 2.1 Presentation Layer (`StudentNameMVC`)
* **Responsibilities:**
  - HTTP requests, Razor Page models, MVC Controllers, ViewModels.
  - SignalR Hub transport (`VivaHub`), client connection management.
  - Authentication cookies and UI-level ModelState validation.
* **Strict Constraints:**
  - **NEVER** inject `AIVESDbContext` or `DbSet<T>` into any Controller, Razor PageModel, or SignalR Hub.
  - **NEVER** inject a DAL Repository directly into Controllers, PageModels, or Hubs.
  - **NEVER** execute raw SQL or LINQ queries against the database in Presentation.
  - Always communicate through **BLL Service interfaces**.

### 2.2 Business Logic Layer (`AIVES.BLL`)
* **Responsibilities:**
  - Business rules, examination session state machines, follow-up count limits.
  - Orchestration between AI services, repositories, and realtime publishers.
  - Security authorization checks (e.g. verifying that student owns the exam attempt).
* **Strict Constraints:**
  - **NEVER** reference EF Core `AIVESDbContext` or SQL Server in BLL.
  - **NEVER** reference HTTP-specific types (`HttpContext`, `HttpRequest`, `ViewData`, `HubCallerContext`) in BLL.
  - Services must depend only on **Repository interfaces** (`AIVES.DAL.Repositories.Interfaces`) and **AI service interfaces** (`AIVES.BLL.Interfaces.Ai`).

### 2.3 Data Access Layer (`AIVES.DAL`)
* **Responsibilities:**
  - EF Core entity mapping and configurations.
  - LINQ queries (`.Where()`, `.OrderBy()`, `.Include()`, `.AsNoTracking()`).
  - Unit of persistence (`SaveChangesAsync`).
  - Foreign key safety and database constraint handling.
* **Strict Constraints:**
  - DAO layer is deprecated and consolidated. Repositories directly use scoped `AIVESDbContext`.
  - Repositories must **NEVER** contain business policy decisions (e.g. max follow-up rules belong in BLL).
  - Repositories and `AIVESDbContext` must **ALWAYS** be registered as **Scoped**. **NEVER** register a Repository as Singleton.

---

## 3. Razor Pages & SignalR Conventions for Assignment 2

1. **Razor Pages Folder Structure:**
   - Place Viva Exam pages under `StudentNameMVC/Pages/Viva/`:
     - `Exam.cshtml` & `Exam.cshtml.cs`: The core interactive viva room.
     - `_ViewImports.cshtml`: Razor Pages tag helpers and namespaces.
2. **SignalR Hub Conventions:**
   - Hubs live in `StudentNameMVC/Hubs/VivaHub.cs`.
   - Hub methods must only handle connection authorization, group enrollment (`viva-session-{id}`), and forwarding requests to BLL.
   - BLL triggers client notifications using `IVivaRealtimePublisher`.
3. **Approved SignalR Event Names:**
   - `SessionStatusChanged`
   - `TranscriptReady`
   - `FollowUpGenerated`
   - `TimeRemainingChanged`
   - `SessionCompleted`
   - `VivaError`

---

## 4. Architecture Drift Prevention

* Do not introduce Minimal APIs, CQRS/MediatR, Clean Architecture Clean-Domain splits, or external microservices.
* Do not introduce foreign JavaScript frontend frameworks (React, Angular, Vue); use vanilla modern JavaScript or lightweight modules with WebAudio API in Razor Pages.
* Preserve existing Group 6 Reporting and Account/News modules without regression.
* All new database tables must integrate with `AIVESDbContext` via EF Core Fluent API.

---

## 5. Testing Expectations

* **Unit Tests (`AIVES.Tests`):**
  - Test BLL state machine transitions and limits using Mock repositories and Mock AI services.
  - Test Repository query and persistence logic against in-memory or integration fixtures.
* **Realtime Tests:**
  - Verify SignalR group authorization and event payloads without requiring active browser audio hardware.
