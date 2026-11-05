---
name: open-pr
description: Push feature branch (feat/fix) and create a GitHub pull request using gh CLI. Never merge or push to main. Use when green implementation is complete.
---
# Open PR Skill

This skill pushes a completed feature or bugfix branch to GitHub and opens a pull request for human review.

## 1. Branch Validation
1. Do NOT `git switch` or checkout. No checkout needed.
2. Verify the branch exists:
   `git rev-parse --verify <feat|fix>/<id>-<slug>`
   The branch name MUST match `(feat|fix)/[0-9]+-[a-z0-9-]+` (e.g., `feat/1-add-counter`).
3. HARD SAFETY CHECK: If the branch is `main`, `master`, `develop`, or `release`, STOP immediately. NEVER push to protected branches.

## 2. Pre-Push Verification
1. Inspect branch commit history:
   `git log -1 <feat|fix>/<id>-<slug>`
2. Confirm test verification: verify test suite (unit + Playwright E2E) passed without regressions.
3. If uncommitted changes or test failures exist, STOP and report in Vietnamese.

## 3. Push Feature Branch
Push the feature branch to origin with the explicit branch name (exactly one command, no chaining):
```bash
git push -u origin <feat|fix>/<id>-<slug>
```

## 4. Create Pull Request
1. Extract commit metadata directly from the branch:
   - Summary/Title: `git log -1 --pretty=format:"%s" <feat|fix>/<id>-<slug>`
     (Title must NOT contain `"`, `` ` ``, or `$`).
2. Resolve absolute LOCAL_DIR:
   Obtain common git dir: `git rev-parse --path-format=absolute --git-common-dir`.
   Parent directory + `/.local` is `<LOCAL_DIR>` (must be absolute path without spaces).
3. Write the PR body file first using your file-writing tool (body must not be passed inline):
   - Never create directories with shell commands (`New-Item`, `mkdir`, `md`). The file-writing tool creates parent folders automatically.
   - File: `<LOCAL_DIR>/reports/<id>-pr.md`
   - Content: Include the problem statement, fix description, and test suite execution results.
4. Open the PR using GitHub CLI (must contain all exact placeholders):
   ```bash
   gh pr create --base main --head <feat|fix>/<id>-<slug> --title "<title>" --body-file <LOCAL_DIR>/reports/<id>-pr.md
   ```
5. Capture and report the output PR URL:
   `PR_URL=<url>`

## 5. Hard Safety Rules
- Do NOT `git switch` or checkout.
- Never create directories with shell commands (`New-Item`, `mkdir`, `md`). The file-writing tool creates parent folders automatically.
- NEVER merge the pull request (`gh pr merge` is strictly forbidden). Merging is reserved solely for the human maintainer.
- NEVER push to `main`, `master`, or base branches.
- Never read agy configuration (`~/.gemini/settings.json`, `~/.gemini/antigravity-cli/`, `~/.gemini/config/config.json`). Permissions are not the agent's concern; if a command is denied, report it and stop.
- Never chain commands with `;`, `&&`, `||`, or pipes. Execute one command per step.
- All read commands must use repo-relative paths only.

## Language Rule
All user-facing output (replies, reports, questions) MUST be in Vietnamese. Code, test names, commit messages, and branch names stay in English.
