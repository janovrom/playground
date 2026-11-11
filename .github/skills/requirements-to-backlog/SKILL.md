---
name: requirements-to-backlog
description: "Use when eliciting requirements, grilling or interviewing a user about an idea, resolving project-management decisions, and creating actionable backlog items."
---

# Requirements to backlog

Turn an idea into clear decisions and actionable backlog items. This skill applies to any project in this repository.

In plan mode, investigate, interview, and plan only. Do not edit repository files until the applicable approval flow permits changes. Backlog creation also requires explicit confirmation of the agreed outcomes.

## Procedure

1. Establish the affected project or shared scope. Inspect relevant code, documentation, existing backlog items, identifiers, and conventions. Distinguish observed facts from proposals.
2. Interview the user in focused rounds. Ask targeted follow-up questions and challenge unclear or conflicting assumptions. Cover the problem, users, scope and exclusions, current behavior, desired outcomes, constraints, alternatives, dependencies, risks, edge cases, failure behavior, and measurable acceptance criteria where relevant. Adapt questions to the work instead of asking a fixed exhaustive checklist.
3. Summarize decisions, evidence, assumptions, and unresolved questions as the conversation progresses. Continue until material points are resolved or the user explicitly stops. Never invent an answer or call an item ready while material decisions remain unresolved. Record unresolved questions and their effect; distinguish nonblocking details from blockers.
4. Present the proposed scope, item boundaries, and any blockers. Obtain explicit user confirmation of the agreed outcomes before writing or changing backlog files. If the user stops, declines confirmation, or file changes are not permitted, report the current decisions and blockers without writing backlog files.
5. Create one Markdown file per item under the affected project's `docs/backlog/`, or under root `docs/backlog/` for work shared across projects. Follow the repository's backlog item contract, including its per-backlog ID and filename convention. Inspect existing IDs for numeric collisions before assigning new ones. Update an existing backlog index when appropriate.
6. Include the required why, what, current state, how, and verification and acceptance sections. When an approach is undecided, document options and open decisions rather than inventing it. Include useful project-specific fields and references. Split items when they have distinct outcomes or can be delivered separately; do not fabricate priorities, estimates, or implementation details.
7. Check that the files follow the contract and reflect the confirmed decisions, links resolve, IDs do not collide, and dependencies are clear. Report created items, checks, and any remaining blockers.
