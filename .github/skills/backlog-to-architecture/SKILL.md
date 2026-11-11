---
name: backlog-to-architecture
description: "Use when promoting a completed backlog item into architecture documentation or reconciling architecture with the item's verified implementation."
---

# Promote backlog items into architecture

Document verified, implemented backlog outcomes as current architecture. This skill applies to any project in this repository.

In plan mode, investigate and plan only. Do not edit repository files until the applicable approval flow permits changes.

## Procedure

1. Identify the requested backlog item and its project or shared scope. Read its requirements, acceptance criteria, dependencies, and relevant architecture documents. Identify the affected behaviors and documents.
2. For each requirement and acceptance criterion, identify implementation evidence and relevant tests. Inspect the code and tests and run the smallest existing test selection that verifies the outcome. Record the evidence and test results. Checked boxes and user assertions alone do not prove completion.
3. If any requirement or criterion is incomplete or lacks evidence, or a required test is missing, failing, skipped, or cannot be run, block promotion for that item. Do not change its architecture documentation or add a promotion link. Report each gap, the relevant evidence, and test status. Ask the user to resolve outstanding decisions or contradictory evidence; do not invent answers. In plan mode, report planned verification rather than claiming success if it cannot be performed under the current restrictions.
4. When all requirements and criteria have implementation evidence and passing relevant tests, and edits are permitted, update the most relevant existing architecture document. Create a focused document only when needed. Use the project's `docs/architecture/`, or root `docs/architecture/` for shared cross-project architecture. Describe verified behavior, boundaries, interfaces, failure modes, and constraints as applicable. Reconcile outdated or conflicting descriptions instead of duplicating them.
5. Follow the repository's technical-language instructions. Include a Mermaid diagram when it clarifies substantive behavior, and base it on verified evidence. Clearly distinguish existing future plans from current implementation.
6. Retain the backlog item, its history, and acceptance criteria. Add a link from it to the relevant architecture documentation. Update navigation links where needed, subject to the user's scope and file-change limits.
7. Check the updated architecture against the evidence, verify relative links, and run relevant existing documentation checks. Report the promoted item, evidence and test results, changed files, and any remaining issues.
