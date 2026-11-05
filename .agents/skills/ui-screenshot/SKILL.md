---
name: ui-screenshot
description: Capture screenshots of app pages with Playwright into the project's local-only folder. Use automatically after a UI task turns green, or when the user asks to capture screens ("chụp", "chụp màn hình", "screenshot").
---
# UI Screenshot
Local dir: LOCAL_DIR = <main repo>/.local, derived at runtime from `git rev-parse --path-format=absolute --git-common-dir` (parent folder of the common .git). This works from any worktree; no env vars or hard-coded paths needed (env prefixes like `$env:X=...;` are blocked by permissions). Never write screenshots into the worktree or any committed folder.
1. Determine pages/states to capture:
   - After a UI task: pages/components touched by task #<id> (from tests + changed files).
   - On user request: pages the user named; if unclear, use the page list in AGENTS.md "Screens" section.
2. Use the committed screenshot suite (created by init-project):
   - First write the request file with your file-writing tool: <LOCAL_DIR>/screenshots/request.json (Never create directories with shell commands like `New-Item`, `mkdir`, `md`; the file-writing tool creates parent folders automatically):
     {"id": "<task id or request-YYYYMMDD-HHMM>", "pages": ["/", "/login", ...]}
   - Node: `npx playwright test --grep @screenshot`
   - .NET: `dotnet test <E2EProject> --filter Category=Screenshot`
   The suite starts the app via webServer/fixture, visits each route, waits for network idle, captures full-page PNG for desktop (1280x800) and mobile (390x844).
3. Output path: <LOCAL_DIR>/screenshots/<id>/<route-slug>-<desktop|mobile>.png
4. Do not modify the screenshot suite to capture new pages; pass routes via request.json. If the suite is missing, STOP and suggest init-project.
5. Report in Vietnamese: list of saved file paths + any page that failed to load (with error line).
6. Screenshots are for human review only; they are NOT test assertions and never committed.
All user-facing output (replies, reports, questions) MUST be in Vietnamese. Code, test names, commit messages, branch names stay in English.
