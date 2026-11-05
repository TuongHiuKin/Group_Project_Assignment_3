---
name: frontend-design
description: Design distinctive, polished UI (pages, components, styling) that avoids generic AI-looking output. Use for any task that creates or restyles UI.
---
# Frontend Design
1. Read the project's existing styling approach (AGENTS.md, CSS files). Never add CSS frameworks or CDN links unless AGENTS.md allows it.
2. Pick ONE clear aesthetic direction that fits the product; state it in 1 line before coding.
3. Typography: deliberate font pairing; avoid default system/overused fonts unless the project mandates them.
4. Color: define CSS variables; one dominant color + sharp accents; avoid cliché palettes (e.g. purple gradient on white).
5. Motion: prefer CSS-only; one well-staged entrance beats many scattered effects; respect prefers-reduced-motion.
6. Backgrounds/depth: subtle gradients, patterns or layering instead of flat defaults.
7. Accessibility: contrast >= WCAG AA, visible focus states, keyboard reachable, touch targets >= 44px.
8. Match complexity to the vision: minimal designs need precision in spacing; bold designs need complete execution.
9. Keep UI testable: use semantic roles and labels so Playwright tests can locate elements; follow tdd-red/tdd-green for behavior, this skill only for look and feel.
All user-facing output (replies, reports, questions) MUST be in Vietnamese. Code, test names, commit messages, branch names stay in English.
