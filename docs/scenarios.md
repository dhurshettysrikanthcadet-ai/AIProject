# Demonstration Scenarios

All scenarios use `POST /api/workflows` with `{ "scenario": "...", "requirement": "..." }`. Read the returned `stages`, `audit`, `decisions`, and `planRevision` to inspect decomposition and execution.

## Greenfield

Example: `{"scenario":"greenfield","requirement":"Build a URL shortener with expiring links and daily click analytics."}`. Understanding flows into architecture; implementation, documentation, tests, and security review follow the dependency graph, with independent branches synchronized at release readiness. The run pauses at human release approval. Verify generated stage outputs, then approve with an actor and rationale. The lifecycle ends at readiness; it does not deploy.

## Brownfield

Example: `{"scenario":"brownfield","requirement":"Add daily analytics to the existing redirect service without changing redirect behavior."}`. Understanding labels the existing API/domain/persistence boundary and the plan includes compatibility/regression checks. The same DAG is used, with tests depending on implementation and documentation running in parallel after architecture. This prototype does not inspect a real repository; brownfield codebase reasoning is illustrative.

## Ambiguous

Example: `{"scenario":"ambiguous","requirement":"Make links fast and secure."}`. The system records assumptions and stops after understanding at `clarification_approval`; architecture cannot run until a human records an approval rationale. Approve with measurable assumptions, inspect the revised artifact, and separately approve release readiness. Replan whenever clarification changes the requirement; previous outputs are invalidated and revision lineage is retained.

## Failure controls

`/safe-stop` cancels active work; `/resume` resumes a safe-stopped run. `/replan` invalidates generated artifacts. `/rollback` clears successful stage outputs for non-completed runs and records a reason. The tests inject transient agent failures to verify the retry ceiling and documentation fallback. No external tools are called by the default agent.