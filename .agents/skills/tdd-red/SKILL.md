---
name: tdd-red
description: Write failing tests (unit + Playwright E2E for UI behavior) that define expected behavior before any implementation. Use at the start of every feature or bug fix (TDD Red).
---
# TDD Red
1. Read AGENTS.md, the task, and related code. All read commands must use repo-relative paths only (no drive letters, no leading `/` or `\`, no `..`).
   Detect unit test framework (xUnit/bUnit for .NET, Vitest/Jest for JS, pytest) and Playwright setup (playwright.config.* for Node, Microsoft.Playwright in a *.Tests.csproj for .NET).
   If NO test project exists: STOP and report "cần init-project trước".
2. Decide test levels:
   - Logic/services/models -> unit tests.
   - Anything the user sees or clicks (pages, components, flows) -> ALSO a Playwright E2E test.
   - Bug fix -> one test that reproduces the bug at the lowest level that shows it.
3. List behaviors: happy path, edge cases, error cases. One behavior per test; descriptive names.
4. Playwright rules:
   - Put E2E tests ONLY in the project's E2E folder (e.g. tests/e2e/ or <Name>.E2E/).
   - Locate by role/label/text (getByRole, getByLabel, getByText); add data-testid only if no accessible locator exists, and list required testids in the report so coder adds them.
   - No fixed sleeps; rely on auto-waiting and web-first assertions (toBeVisible, toHaveText, toHaveURL).
   - Each test independent: own setup, no shared state, no order dependency.
   - Base URL from config/env, never hard-coded ports.
5. Write tests ONLY in test folders. Never create or edit implementation files.
6. Run the run-tests skill (.agents/skills/run-tests/SKILL.md). One command per step; never chain commands with `;`, `&&`, `||`, or pipes. Confirm NEW tests fail for the RIGHT reason (missing element/behavior, not syntax or setup errors). Existing tests must still pass.
7. Staging, commit, and branch creation (CRITICAL ORDER - auto-branch is deleted on kill):
   a. Stage the new and modified test files (relative paths only, one command):
      `git add <relative paths of test files>`
   b. Verify staged status:
      `git status --porcelain`
   c. Commit failing tests on your current (auto) branch:
      `git commit -m "test(<scope>): add failing tests for <behavior> (#<id>)"`
      Sanitization rules (STRICT):
      - Remove any `"`, `` ` ``, `$`.
      - Replace `;`, `|`, `&`, `>`, `<` with commas or words (e.g. `->` to `to`, `&` to `and`, `;` to `,`).
      - No newline inside `-m`.
   d. Then create the named feature/fix branch at HEAD:
      `git branch <feat|fix>/<id>-<slug> HEAD`
      (Branch name MUST strictly match regex `(feat|fix)/[0-9]+-[a-z0-9-]+` in lowercase and hyphens).
   e. Verify the named branch exists:
      `git rev-parse --verify <feat|fix>/<id>-<slug>`
   f. Record and output: `TEST_BASELINE=<commit hash>`.
   NEVER create the named branch before committing.
8. Write <LOCAL_DIR>/reports/<id>.md (tests list + what each asserts + run result). Never create directories with shell commands (`New-Item`, `mkdir`, `md`); the file-writing tool creates parent folders automatically. Report: each test name + what it asserts (unit/E2E), required data-testids, run result, TEST_BASELINE. Then STOP - user must approve tests before implementation.
All user-facing output (replies, reports, questions) MUST be in Vietnamese. Code, test names, commit messages, branch names stay in English.
