---
name: init-project
description: Prepare a repository for the TDD workflow (works with any coding agent; agy orchestrator optional) - verify toolchain, create docs/ and local-only .local/ folders, create unit + Playwright E2E test projects and screenshot suite, write AGENTS.md and .agents/ files. Use once per new project ("tạo base", "init project") or when tests/AGENTS.md are missing.
---
# Init Project
1. Detect stack: *.sln/*.csproj (.NET), package.json (Node), pyproject.toml/requirements.txt (Python), pom.xml/build.gradle (Java).
   All read commands must use repo-relative paths only (no drive letters, no leading `/` or `\`, no `..`). Never chain commands with `;`, `&&`, `||`, or pipes.
2. Verify toolchain (`dotnet --version`, `node -v`). Report missing tools; never install system software.
3. Build once. Report errors; do not fix unrelated code.
4. Create COMMITTED folders (add a README.md stub in each so git keeps them; never create directories with shell commands like New-Item, mkdir, md - the file-writing tool creates parent folders automatically):
   docs/product/      requirements, user stories
   docs/architecture/ architecture, diagrams
   docs/adr/          decision records (NNNN-title.md)
   docs/api/          API descriptions
   docs/testing/      test strategy, E2E flow list
5. Create LOCAL-ONLY folder .local/ (never committed), one per project (file-writing tool creates parent folders automatically):
   .local/queue.md            task queue file
   .local/screenshots/        UI screenshots (per task id or request)
   .local/playwright/test-results/   failure screenshots + traces
   .local/playwright/report/  HTML report
   .local/reports/            per-task reports for the user (<id>.md)
   .local/notes/              research notes, drafts
   .local/tmp/                temp files (only place agents may propose deleting)
6. .gitignore: add ".local/" (this covers .local/queue.md and all local artifacts; .agents/queue.md is no longer used).
7. If test projects are missing, PROPOSE and WAIT for user approval (run each command as a separate step, no chaining):
   .NET unit: `dotnet new xunit -o <Name>.Tests` (+ bUnit for Blazor), then `dotnet sln add <Name>.Tests`, then `dotnet add <Name>.Tests reference <Name>`.
   .NET E2E: `dotnet new nunit -o <Name>.E2E` + Microsoft.Playwright.NUnit (package add needs approval); fixture starts the app on a free port; resolves LOCAL_DIR from `git rev-parse --path-format=absolute --git-common-dir` (parent)/.local.
   Node unit: Vitest or Jest. Node E2E: @playwright/test with playwright.config.ts:
     testDir "tests/e2e"; use.baseURL from env; headless;
     screenshot "only-on-failure"; trace "retain-on-failure";
     const LOCAL_DIR = path.join(path.dirname(execSync("git rev-parse --path-format=absolute --git-common-dir").toString().trim()), ".local");
     outputDir = LOCAL_DIR + "/playwright/test-results";
     reporter [["line"],["html",{ outputFolder: LOCAL_DIR + "/playwright/report", open: "never" }]];
     webServer { command: <dev cmd>, url, reuseExistingServer: true }.
   Visual snapshot comparison (toHaveScreenshot) is OFF by default.
8. Create the committed screenshot suite (tagged @screenshot / Category=Screenshot) used by the ui-screenshot skill (.agents/skills/ui-screenshot/SKILL.md):
   resolves LOCAL_DIR the same way; reads <LOCAL_DIR>/screenshots/request.json {id, pages}; if absent, skips silently;
   visits each route; saves full-page PNG desktop 1280x800 + mobile 390x844 to <LOCAL_DIR>/screenshots/<id>/<route-slug>-<viewport>.png. It must never fail the normal test run (excluded by tag/filter by default).
9. Tell the user to install Playwright browsers ONCE (machine-wide) and wait for confirmation.
10. AGENTS.md & docs/: if AGENTS.md and docs/ already exist (e.g. created by analyze-project), REUSE them; only add missing sections (commands, Screens, LOCAL_DIR note, test folders). Never overwrite existing content without showing a diff and asking. Otherwise, create AGENTS.md with: overview, architecture tree, exact build / unit / e2e / screenshot / run commands, test folders,
    how the app starts for E2E, "Screens" section (route list), conventions, do-not-touch areas, and a note:
    "LOCAL_DIR = <main repo>/.local, resolved via git common dir (works from worktrees)".
11. Install the portable skills INTO THIS PROJECT (so any coding agent can use them without the agy orchestrator):
    - Source library: ~/.agent-skills/portable/skills/  (installed by install.ps1; the copied set includes analyze-project as it is in the library now). If missing, STOP and tell the user.
    - Copy every skill folder to <repo>/.agents/skills/   (read by agy/Antigravity, Codex, Cursor, Gemini CLI, Copilot and others)
    - Copy the same folders to  <repo>/.claude/skills/   (read by Claude Code)
    - Do not overwrite a project skill that already exists with different content; list conflicts and ask.
    - Record versions: write <repo>/.agents/skills/SKILLS-VERSION.md (date + list of copied skills).
12. Instruction files for different agents:
    - AGENTS.md is the single source of truth (read by most agents).
    - CLAUDE.md containing one line: "@AGENTS.md" (Claude Code imports it).
13. agy-only extras (skip if the user does not use agy): create .agents/routing.md (| signal | agent | skill |) and .local/queue.md (gitignored via .local/, format includes conv column: `| id | task | phase | agent | conv | branch | TEST_BASELINE | owned files | status | PR |`).
14. Ensure lockfiles are committed.
15. Staging and commit:
    a. Stage everything init created, using relative paths:
       `git add AGENTS.md CLAUDE.md .gitignore docs .agents .claude <unit test project folder> <e2e test project folder> <solution or package files changed>`
    b. Verify staged status before commit:
       `git status --porcelain`
       Confirm nothing under .local/ is staged.
    c. Commit:
       `git commit -m "chore: init TDD setup and project skills"`
       (Sanitize: remove `"`, `` ` ``, `$`; replace `;`, `|`, `&`, `>`, `<` with words or commas).
    Skills MUST be committed: agy subagents work in git worktrees and only see committed files; other agents/teammates also get them via git.
16. If the user uses agy: run skill setup-stack to propose allow rules, INCLUDING write_file(<absolute LOCAL_DIR>/) for this machine so subagents can write screenshots/reports.
All user-facing output (replies, reports, questions) MUST be in Vietnamese. Code, test names, commit messages, branch names stay in English.
