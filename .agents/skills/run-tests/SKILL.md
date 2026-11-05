---
name: run-tests
description: Restore dependencies, run the full unit + Playwright E2E suite, parse pass/fail counts, and produce the standard commit message body. Use whenever tests must be run or reported.
---
# Run Tests
0. LOCAL_DIR = <main repo>/.local, derived at runtime from `git rev-parse --path-format=absolute --git-common-dir` (parent folder of the common .git). This works from any worktree; no env vars or hard-coded paths needed (env prefixes like `$env:X=...;` are blocked by permissions). Playwright config / .NET fixture already resolve it; just run the commands below. Results land in the main repo's .local, not the worktree.
1. Restore deps (fresh worktrees have none): One command per step (never chain with `;`, `&&`, `||`, or pipes).
   - .NET: First run `dotnet restore`, then run `dotnet build`.
   - Node: Run `npm ci`.
   - Python: Run per project virtual environment.
   Prefer AGENTS.md commands. All read commands must use repo-relative paths only.
2. Playwright browsers are installed machine-wide by the user. If a run fails with "Executable doesn't exist" / browsers missing: STOP and ask the user to run the install once
   (Node: `npx playwright install`; .NET: `pwsh <TestProject>/bin/Debug/<tfm>/playwright.ps1 install`). Never run install yourself.
3. Unit tests: .NET `dotnet test <UnitTestProject>`; Node `npm test`; Python `pytest`.
4. E2E tests:
   - Node: `npx playwright test --reporter=line`. Rely on webServer in playwright.config to start the app; if absent, STOP and report (do not start servers manually in the background).
   - .NET: `dotnet test <E2ETestProject>`; app startup must be handled by the test fixture as documented in AGENTS.md.
   - Run headless. Never use --headed, --debug, --ui, codegen or --update-snapshots.
   - Exclude the @screenshot suite from normal runs (Node: --grep-invert @screenshot; .NET: --filter Category!=Screenshot).
5. On E2E failure collect: failing test names, first error line, path to trace/screenshot under <LOCAL_DIR>/playwright/test-results/. Do not open the HTML report (`show-report`).
6. Parse counts separately for unit and E2E.
7. Output exactly:
   TEST_CMD=<commands>
   TEST_RESULT=unit <p> passed, <f> failed; e2e <p> passed, <f> failed, <s> skipped
8. Commit message format:
   Commits must use one or more `-m "<msg>"` arguments (first `-m` = summary line, then one `-m` per body section).
   ```bash
   git commit -m "<type>(<scope>): <summary> (#<id>)" -m "Problem: <old bug or requirement>" -m "Fix: <what was changed and why>" -m "Tests: <TEST_CMD> to <TEST_RESULT>, new tests: unit <n>, e2e <n>" -m "Task: #<id>"
   ```
   Sanitization rules (STRICT):
   - Remove any `"`, `` ` ``, `$`.
   - Replace `;`, `|`, `&`, `>`, `<` with commas or words (e.g. `->` becomes `to`, `&` becomes `and`, `;` becomes `,`, `|` becomes `,`).
   - Example count format: "unit 3 passed, 0 failed, e2e 1 passed, 0 failed, 0 skipped".
   - No newline inside any `-m` string (use separate `-m` arguments for paragraphs).
   - No `-F` flag, no heredocs, no file-based commit messages.
9. Never commit anything under .local/.
All user-facing output (replies, reports, questions) MUST be in Vietnamese. Code, test names, commit messages, branch names stay in English.
