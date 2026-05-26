---
alwaysApply: true
---

# Task Execution Workflow

Use this workflow for all feature, bugfix, and refactoring tasks.

## Phase 1: Understand

- Restate the requested outcome precisely.
- Identify the current behavior from code, not assumption.
- Trace the relevant execution path.
- List the smallest set of files that are likely involved.

## Phase 2: Plan

Before changing code, provide:
1. root cause or implementation target
2. files to modify
3. minimal plan

Do not start broad edits without this plan.

## Phase 3: Implement

- Apply the smallest coherent change.
- Keep edits localized.
- Reuse repository patterns.
- Avoid speculative cleanups.
- Do not modify unrelated files.

## Phase 4: Validate

- Check for compile, lint, and test impact.
- Review whether the change introduced a second path or duplicate abstraction.
- Confirm exception handling and logging remain correct.

## Phase 5: Report

At the end, report:
- what changed
- why those files changed
- risks or follow-up debt
- validation status