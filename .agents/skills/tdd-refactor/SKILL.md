---
name: tdd-refactor
description: Refactor green code for readability and structure without changing behavior; unit and Playwright E2E tests must stay green. Use after TDD Green when the user asks for cleanup.
---
# TDD Refactor
0. Switch to Task Branch (MANDATORY FIRST STEP):
   `git switch <feat|fix>/<id>-<slug>`
   Verify HEAD is on the task branch. All refactor commits must go on this named branch.
1. Precondition: full suite (unit + E2E) is green. If not, STOP.
2. Identify small refactors inside the task's files only: naming, duplication, long methods, dead code.
3. Do not rename/remove data-testid, roles or labels used by E2E tests.
4. One refactor at a time; run the run-tests skill (.agents/skills/run-tests/SKILL.md) after each; revert any step that breaks a test.
5. NEVER edit tests. Run the full test suite (unit + E2E) before committing.
6. Staging and commit on the named branch:
   a. Stage modified files (relative paths only, one command):
      `git add <relative paths of changed files>`
   b. Verify staged status before commit:
      `git status --porcelain`
      Refuse to stage any file under the test folders.
   c. Commit using multiple `-m` arguments:
      `git commit -m "refactor(<scope>): <summary> (#<id>)" -m "Problem: <reason for refactor>" -m "Fix: <what was refactored>" -m "Tests: <TEST_CMD> to <TEST_RESULT>" -m "Task: #<id>"`
      (Sanitize: remove `"`, `` ` ``, `$`; replace `;`, `|`, `&`, `>`, `<` with commas or words; no newlines inside `-m`).
7. Guard check (mandatory, runs AFTER the implementation commit):
   `git diff --name-only <TEST_BASELINE> HEAD -- <unit-test-folder> <e2e-test-folder> <playwright config file if any>`
   Output must be empty; otherwise report "VI PHẠM: đã sửa test" and stop.
All read commands must use repo-relative paths only. Never chain commands with `;`, `&&`, `||`, or pipes.
All user-facing output (replies, reports, questions) MUST be in Vietnamese. Code, test names, commit messages, branch names stay in English.
