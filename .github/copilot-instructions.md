# Repository instructions

## Technical language

- Write clear, concise technical English inspired by ASD-STE100 principles. Use short sentences, consistent terminology, active voice, and direct instructions.
- Do not claim formal ASD-STE100 compliance without an authoritative reference and an appropriate compliance review. Preserve exact identifiers, commands, quoted text, and necessary technical terms.

## Diagrams

- For substantive code or architecture explanations and changes, provide a Mermaid diagram when it makes relationships, control flow, or behavior easier to understand. Do not force a diagram for a trivial change.
- Base diagrams on verified behavior. Label proposed designs and assumptions so they are not confused with the current implementation.

## Documentation placement

- Place project-specific architecture, backlog, and research documentation under that project's `docs/architecture/`, `docs/backlog/`, and `docs/research/`, respectively.
- Place documentation shared across projects in the corresponding directories under the repository-root `docs/`.
- Classify documentation by its main purpose. Keep useful entry points such as project READMEs where they belong. If ownership or classification is unclear, ask before choosing a location.
- Put each new backlog item in its own Markdown file. An index may summarize and link to items.

## Backlog items

- Use an ID `BL-` followed by at least four digits, padded with leading zeros as needed, and a matching lowercase filename such as `bl-0006.md`. IDs are unique within each project's backlog or the shared root backlog. Check for numeric ID collisions, including differently padded IDs.
- Include these sections in every item:
  - **Why:** the problem, motivation, and impact.
  - **What:** the desired outcome, scope, constraints, and applicable dependencies.
  - **Current state:** observed behavior and supporting evidence or references; identify unverified claims.
  - **How:** the implementation approach, or options and open decisions when it is not agreed.
  - **Verification and acceptance:** verification methods and measurable acceptance criteria. Record missing methods or thresholds as open questions.
- Preserve useful project-specific fields. Distinguish facts, proposals, and unresolved questions; do not invent missing details or call an item ready while material decisions remain unresolved.

## Verification

- Back every code change with a successful build and passing tests for the affected behavior before reporting it as complete.
- Run the narrowest build and test commands that cover the change. If validation fails or cannot run, report that clearly and do not claim the change is verified.
