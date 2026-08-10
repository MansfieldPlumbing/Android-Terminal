# Contributing to Remedy

Thank you for contributing to Remedy. To preserve project governance and truthful status, all contributions must follow these standards:

## Governance & Rules

1. **Branch Scoping**: Limit each branch to exactly one invariant.
2. **Commit Scoping**: Focus each commit on a single semantic objective.
3. **Explicit File Boundary**: Do not modify files outside the explicitly allowed list for your task.
4. **Test Integrity**: Never modify test assertions merely to make a failing implementation pass.
5. **Error Handling**: Do not ignore timeouts or return codes.
6. **Executable Proof**: Every source and status claim must be backed by an executable assertion; unverified claims are not permitted.
7. **Semantic Changes**: If a fix requires changing approved semantics, stop and request maintainer review.
8. **Git Safety**:
   - Never push directly to `main`.
   - Never force push.
   - Never amend the recovered baseline commit or tag.

## Submission Workflow

- Open a branch following the `type/description` pattern (e.g., `chore/truthful-baseline`).
- Fill out `.github/pull_request_template.md` completely when opening a pull request.
- Ensure all clean build commands and test commands pass before requesting review.
