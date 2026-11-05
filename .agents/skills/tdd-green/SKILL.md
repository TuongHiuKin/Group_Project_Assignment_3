---
name: tdd-green
description: Implement the minimal code to make approved failing unit and Playwright E2E tests pass, without modifying tests. Use after the user approves tests (TDD Green).
---
# TDD Green
Input required: branch name `<feat|fix>/<id>-<slug>` with approved tests + `TEST_BASELINE=<hash>`. If missing, STOP and ask.

0. Switch to Task Branch (MANDATORY FIRST STEP):
   a. Switch to the named task branch:
      `git switch <feat|fix>/<id>-<slug>`
   b. Verify HEAD is TEST_BASELINE or a descendant of it:
      `git rev-parse HEAD`
      (All implementation commits MUST go onto this named branch).

1. Run the run-tests skill (.agents/skills/run-tests/SKILL.md) to see current failures. One command per step; never chain commands with `;`, `&&`, `||`, or pipes.
2. Implement the MINIMUM code to make them pass. No extra features or speculative abstractions.
   Add data-testid / accessible names exactly as the tests expect.
3. NEVER create, edit, rename or delete files in unit OR E2E test folders, playwright.config.*, or test fixtures.
4. If an E2E test fails: read the trace/screenshot under <LOCAL_DIR>/playwright/test-results/ before changing code. Do not "fix" by adding waits to tests.
5. Run the run-tests skill (.agents/skills/run-tests/SKILL.md) after each meaningful change. Max 3 full attempts; if still failing, STOP and report failing tests + hypothesis + trace path.
6. Run the FULL suite (unit + E2E). All must pass (no regressions).
7. Staging and commit on the named branch:
   a. Stage modified implementation files (relative paths only, one command):
      `git add <relative paths of changed files>`
   b. Verify staged status before commit:
      `git status --porcelain`
      Refuse to stage any file under the test folders.
   c. Commit using the multiple `-m` format from run-tests:
      `git commit -m "<type>(<scope>): <summary> (#<id>)" -m "Problem: <old bug or requirement>" -m "Fix: <what was changed and why>" -m "Tests: <TEST_CMD> to <TEST_RESULT>, new tests: unit <n>, e2e <n>" -m "Task: #<id>"`
      (Sanitize: remove `"`, `` ` ``, `$`; replace `;`, `|`, `&`, `>`, `<` with commas or words; no newlines inside `-m`).
8. Guard check (mandatory, runs AFTER the implementation commit):
   `git diff --name-only <TEST_BASELINE> HEAD -- <unit-test-folder> <e2e-test-folder> <playwright config file if any>`
   Output must be empty; otherwise report "VI PHẠM: đã sửa test" and stop.
9. If the task changed UI: run the ui-screenshot skill (.agents/skills/ui-screenshot/SKILL.md) with id=<id>.
10. Write report <LOCAL_DIR>/reports/<id>.md: summary, files changed, test results, screenshot paths, open issues. Never create directories with shell commands (`New-Item`, `mkdir`, `md`); the file-writing tool creates parent folders automatically.
All read commands must use repo-relative paths only. Never chain commands with `;`, `&&`, `||`, or pipes.
All user-facing output (replies, reports, questions) MUST be in Vietnamese. Code, test names, commit messages, branch names stay in English.
