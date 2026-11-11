---
name: documentation-simplification
description: "Use when reviewing, auditing, simplifying, consolidating, or improving project documentation for clarity, accuracy, structure, and unnecessary complexity."
---

# Review and simplify documentation

Review documentation rigorously and simplify it without changing its intended meaning. This skill applies to any project in this repository.

In plan mode, investigate and plan only. Do not edit or move repository files until the applicable approval flow permits changes.

## Procedure

1. Identify the requested documents, audience, purpose, and requested outcome. A review or audit without an edit request is report-only. A request to simplify or improve documentation permits edits within its scope, even when it also requests a review, unless the user restricts changes. Inspect relevant code, related documentation, and existing conventions before evaluating technical claims.
2. Review for incorrect or unsupported claims, stale information, ambiguity, duplication, unnecessary detail, poor organization, missing constraints, and broken links. Distinguish verified facts from assumptions and proposed behavior.
3. For report-only requests, present findings with evidence and stop without editing. When simplification or improvement is requested and edits are permitted, edit within the requested scope. Use concise, consistent technical English and a structure suited to the audience. Remove redundancy and improve navigation, but preserve technical meaning, important constraints, identifiers, commands, examples, and useful references.
4. Place project-specific architecture, backlog, and research documents in the corresponding project `docs/` directories; place cross-project documentation under root `docs/`. Update affected links when moving files. Do not move unrelated documents without a reason.
5. Use Mermaid diagrams when they clarify substantive code or architecture. Label proposed behavior and do not invent facts to fill gaps. Flag uncertainty or conflicting evidence rather than presenting it as resolved.
6. Verify factual claims against available sources, check affected links and document structure, and run any relevant existing documentation checks. Report findings or substantive edits, unresolved issues, and checks performed.
