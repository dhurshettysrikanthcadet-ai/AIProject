# Engineering Approach, Validation, and Risks

## AI-assisted engineering approach

The prototype separates orchestration policy from stage execution through `IStageAgent`. The shipped rule-based agent makes every scenario deterministic and reproducible. An optional OpenAI-compatible adapter is available only after explicit external-processing opt-in; it has no tools or deployment credentials, treats requirements as untrusted data, bounds output size, and is subject to the same retries and approval gates. A production adapter should emit schema-validated artifacts and pass outputs through policy, security, and executable test gates. The human remains accountable for clarification and release approval. External processing transmits requirement content and must be approved under the operator's data-handling policy.

## Validation

Run `dotnet test AgenticUrlShortener.slnx`. Tests cover destination allow-listing and embedded-credential rejection, expiry, persistence/reload and analytics, scenario gates, parallel branch completion, retry limits, visible fallback, safe-stop/resume, replan lineage, and scope/subject claim evaluation. Build with `dotnet build AgenticUrlShortener.slnx`.

Before production, add OIDC-provider integration and load tests, concurrent multi-process persistence tests, crash/restart workflow recovery, property/fuzz testing for URL parsing, threat modeling, dependency scanning, separation-of-duties enforcement, and a failure-injection suite against real storage/model adapters.

## Assumptions and trade-offs

- Link codes are random 72-bit URL-safe identifiers with collision checks and bounded retries.
- JSON storage favors zero-dependency setup and single-instance reviewability over throughput, transactional guarantees across processes, or horizontal scaling.
- Redirects are HTTP 302. Expired links return 404 and remain stored; expiry cleanup is not implemented.
- Daily analytics use UTC and at most 1,000 retained click timestamps per link. Lifetime totals remain exact only while writes succeed; no visitor identifiers are stored.
- Workflow state, audit events, and metrics are volatile. The reported MTTR is measured from a recorded failure to an explicit replan, not from infrastructure incident declaration to service restoration.
- Retryable agent failures are bounded, but idempotency and remote call cost controls must be added to a real model adapter.

## Limitations and safety

Workflow authentication and scope authorization use an external OIDC provider configured by `Authentication:Authority` and `Authentication:Audience`; when absent, workflow routes deny requests. There is no local identity provider or provider integration test. Durable workflow state, deployment integration, artifact sandbox, and autonomous code editing are also absent. Audit actors are sourced from validated token subjects. The orchestrator's stages produce reviewable text artifacts; the URL service and its tests are real code, while generic runs do not rewrite a repository or execute generated tests. Do not treat a completed run as a production release approval.