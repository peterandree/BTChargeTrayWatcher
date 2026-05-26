# Issue Implementation Prompt

Use repository rules and project instructions.

Task:
<replace with issue title and description>

Constraints:
- Work only on this issue.
- Do not refactor unrelated areas.
- Reuse existing patterns in the repository.
- Keep the change on the production path.
- Do not swallow exceptions.
- Do not suppress warnings.

Required process:
1. Explain the current behavior or root cause from the code.
2. List the exact files that should change.
3. Propose the minimal implementation plan.
4. Implement the change.
5. Update or add tests where appropriate.
6. Summarize changed files, risks, and validation status.

Output format:
- Root cause / target behavior
- Files to change
- Plan
- Implementation
- Validation
- Follow-up debt