---
name: github-ci-evidence
description: "Use when running or inspecting GitHub Actions to verify builds, platform-specific tests, or backlog acceptance against an exact revision."
---

# Verify work with GitHub CI

Use this workflow to collect evidence for a specific revision. A green run
alone does not prove that the required tests ran or passed.

## Identify the required evidence

1. Read the acceptance criteria and relevant workflow. List each required
   platform, build step, test name, and expected outcome. Identify which tests
   are expected to run or skip on each platform.
2. Record the repository, workflow, event, branch or pull request, and expected
   source commit. Check the worktree for uncommitted changes. A GitHub run
   cannot verify changes that are not in its checkout.
3. Before committing, pushing, creating a pull request, or dispatching a
   workflow, obtain any required user authorization. Do not include unrelated
   changes or secrets. If publication is not authorized, report that CI
   verification is blocked.

## Find or run the workflow

1. Use `gh workflow list --repo OWNER/REPO` and
   `gh run list --repo OWNER/REPO --workflow WORKFLOW --limit 20` to find
   relevant runs. Use `gh run view RUN_ID --repo OWNER/REPO --json
   url,event,headBranch,headSha,status,conclusion,jobs` to inspect a run.
2. When dispatch is authorized, first check that the workflow supports
   `workflow_dispatch` and is present on the default branch. Dispatch the
   published ref with `gh workflow run WORKFLOW --repo OWNER/REPO --ref REF`.
   Otherwise use the repository's existing push or pull-request trigger.
   Monitor the identified run with `gh run watch RUN_ID --repo OWNER/REPO`.
3. Confirm the run checked out the expected revision. For push or manual runs,
   compare the actual checkout SHA with the expected source commit. For pull
   requests, record the source head SHA, base SHA, and actual checkout SHA;
   verify that a synthetic merge checkout contains the expected head and base.
   Inspect the checkout log or a recorded checkout artifact. Do not treat the
   run's `headSha` alone as proof of which commit was checked out. If the
   source, base, or checkout cannot be matched, reject the run as evidence.

## Verify individual results

1. Inspect every required job and build/test step. A missing, failed,
   cancelled, or skipped required job or step is not passing evidence.
2. Download the run's platform-specific result artifacts with `gh run
   download RUN_ID --repo OWNER/REPO --dir DIRECTORY`. Parse the native report
   format for that project. For TRX, inspect `UnitTestResult` test names and
   `outcome` attributes, and match them to the relevant platform. Verify each
   required test is present and `Passed`, not skipped or absent. Check the
   report belongs to the job and revision being assessed.
3. If reports or logs are missing, empty, expired, ambiguous, or do not prove
   the required behavior, report the gap. A passing aggregate count does not
   prove that a platform-specific test executed. Inspect test code when needed
   to detect tests that return early while appearing to pass.
4. Record the run URL, event, source and checkout SHA, base SHA for pull
   requests, job names and conclusions, build results, artifact names, and
   individual test names and outcomes. Only mark an acceptance criterion
   complete when all of its requirements have matching evidence.

The modular-monolith CI workflow produces `test-results-ubuntu-latest` and
`test-results-windows-latest` artifacts containing TRX and checkout SHA files.
Other projects may use different workflows or report formats.
