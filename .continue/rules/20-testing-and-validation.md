---
alwaysApply: true
---

# Testing And Validation

## Test expectations

- Any behavior change requires validation.
- Add or update automated tests when the repository already contains tests for the affected area.
- If tests do not exist, keep the code testable and state the most appropriate place to add tests later.

## Validation flow

Before finalizing a change:
1. Check for impacted unit tests.
2. Check for impacted integration tests.
3. Check for compile or type errors.
4. Check for lint/format issues.
5. State what was validated and what was not validated.

## Output requirement

After implementation, always provide:
- changed files
- behavior impact
- test impact
- exact validation commands to run locally

## Honesty rule

- Do not claim successful execution of tests, builds, or linters unless they were actually run and passed.