---
alwaysApply: true
---

# Coding Standards

## General

- Follow the naming, formatting, and structural conventions already present in this repository.
- Prefer explicit, readable code over clever code.
- Keep methods/functions focused and single-purpose.
- Remove dead code created by the change.
- Do not add placeholder TODO implementations unless explicitly requested.

## Errors and warnings

- Never swallow exceptions silently.
- Never suppress compiler or linter warnings as a shortcut.
- Fix root causes instead of masking symptoms.
- Surface errors with enough context for diagnosis.

## API and contracts

- Preserve public behavior unless the task explicitly changes requirements.
- Keep input/output contracts stable where possible.
- When changing contracts, update all impacted call sites and tests.

## Dependencies

- Reuse shared clients/services where the codebase already uses that pattern.
- Do not create duplicate clients or duplicate integration wrappers.
- Avoid adding a new package unless it is clearly necessary.

## Refactoring discipline

- Refactor only as much as needed to make the required change correct and maintainable.
- Separate opportunistic cleanup from required implementation work.