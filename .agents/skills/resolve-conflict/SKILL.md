---
name: resolve-conflict
description: Pre-check merge conflicts using git merge-tree, automatically resolve mechanical conflicts, and stop on logic conflicts. Never push to main. Use before PR creation or during branch sync.
---
# Resolve Conflict Skill

This skill inspects and resolves git merge conflicts between branches safely, distinguishing mechanical conflicts from semantic logic conflicts.

## 1. Conflict Pre-Check
Inspect merge conflicts safely without altering working trees or committing state.
Execute each step as a separate unchained command (never use `$()`, `;`, `&&`, or pipes):
1. First, find the common merge base:
   ```bash
   git merge-base origin/main HEAD
   ```
2. Next, execute the merge-tree inspection with the resulting `<base-hash>`:
   ```bash
   git merge-tree <base-hash> origin/main HEAD
   ```
   (Or in Git 2.38+: `git merge-tree --write-tree origin/main HEAD`)
Examine the merge-tree output to determine whether conflicts exist and which files are affected.

## 2. Conflict Classification
Categorize each conflicting file:
1. **Mechanical Conflicts**:
   - Whitespace differences, indentation, or code formatting.
   - Independent import/include statements added in different orders.
   - Non-overlapping additions or independent changes in different sections of the same file.
2. **Logic Conflicts**:
   - Competing modifications to the same function or business logic block.
   - Breaking changes in method signatures, parameters, or return types.
   - Incompatible architectural or dependency updates.

## 3. Resolution Procedure
1. For Mechanical Conflicts:
   - Carefully edit the conflicted file to include both valid changes while preserving syntax and conventions.
   - Run the test suite using `.agents/skills/run-tests/SKILL.md`.
   - All tests (unit + E2E) MUST pass completely before proceeding.
   - Commit the resolution on the task branch:
     `git commit -m "chore: resolve mechanical merge conflicts"`
     (Message must not contain `"`, `` ` ``, or `$`).
2. For Logic Conflicts:
   - STOP immediately.
   - Do NOT attempt to guess semantic intent.
   - Report the conflicting files and line ranges to the user in Vietnamese.
   - Present the alternatives clearly and request human decision.

## 4. Hard Safety Rules
- Any `git merge` that changes a branch is an ASK command and must be presented to the user for explicit approval. It must NEVER be executed automatically or retried via alternative/workaround means.
- NEVER push directly to `main` or `master`.
- NEVER use force push (`--force`, `-f`, `--force-with-lease`).
- All resolutions must be verified by a green test run before completing.
- Execute one command per step; never chain commands.
- All read commands must use repo-relative paths only.

## Language Rule
All user-facing output (replies, reports, questions) MUST be in Vietnamese. Code, test names, commit messages, and branch names stay in English.
