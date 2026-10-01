namespace AgenticUrlShortener.Api.Workflows;

public enum WorkflowScenario { Greenfield, Brownfield, Ambiguous }
public enum WorkflowRunStatus { Running, AwaitingApproval, Succeeded, Failed, SafeStopped, RolledBack }
public enum WorkflowStageStatus { Pending, Running, WaitingApproval, Succeeded, Failed, Skipped }

public sealed record StartWorkflowRequest(string Scenario, string Requirement);
public sealed record ApprovalRequest(string Note);
public sealed record ReplanRequest(string Requirement, string Reason);
public sealed record RollbackRequest(string Reason);

public sealed class WorkflowStage(string id, string name, IReadOnlyList<string> dependsOn, bool requiresApproval = false)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public IReadOnlyList<string> DependsOn { get; } = dependsOn;
    public bool RequiresApproval { get; } = requiresApproval;
    public WorkflowStageStatus Status { get; set; } = WorkflowStageStatus.Pending;
    public int Attempts { get; set; }
    public string? Output { get; set; }
    public string? Error { get; set; }
    public bool UsedFallback { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed record WorkflowAuditEvent(DateTimeOffset At, string Action, string Actor, string Details, int PlanRevision);
public sealed record WorkflowDecision(DateTimeOffset At, string StageId, string Actor, string Decision, string Rationale, int PlanRevision);

public sealed class WorkflowRun
{
    public required string Id { get; init; }
    public required WorkflowScenario Scenario { get; init; }
    public required string Requirement { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
    public WorkflowRunStatus Status { get; set; } = WorkflowRunStatus.Running;
    public int PlanRevision { get; set; } = 1;
    public bool StopRequested { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? FailedAt { get; set; }
    public List<double> RecoveryDurationsMs { get; } = [];
    public List<WorkflowStage> Stages { get; } = [];
    public List<WorkflowAuditEvent> Audit { get; } = [];
    public List<WorkflowDecision> Decisions { get; } = [];
}

public sealed record WorkflowMetrics(int TotalRuns, int TerminalRuns, double SuccessRate, int RetryCount, int RollbackCount, double? MeanTimeToRecoveryMilliseconds, double? MeanEndToEndLatencyMilliseconds);
public sealed record StageExecutionContext(WorkflowScenario Scenario, string Requirement, IReadOnlyDictionary<string, string> CompletedOutputs);
public sealed record StageExecutionResult(string Output);

public interface IStageAgent
{
    Task<StageExecutionResult> ExecuteAsync(string stageId, StageExecutionContext context, CancellationToken cancellationToken);
}

public sealed class RuleBasedStageAgent : IStageAgent
{
    public Task<StageExecutionResult> ExecuteAsync(string stageId, StageExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scenarioNote = context.Scenario switch
        {
            WorkflowScenario.Greenfield => "Create a new bounded service with API, persistence, tests, and operational controls.",
            WorkflowScenario.Brownfield => "Map the change to existing API, domain, persistence, and regression-test boundaries before editing.",
            _ => "Ambiguities are recorded as assumptions; a human must confirm them before design proceeds."
        };
        var output = stageId switch
        {
            "understand" => $"Normalized requirement: {context.Requirement}\nScenario: {context.Scenario}. {scenarioNote}",
            "architecture" => "HTTP API -> services -> repository; workflow engine -> bounded stage agent. Define contracts and failure boundaries.",
            "implementation" => "Implementation artifact: isolate API changes behind interfaces; validate inputs and preserve compatibility.",
            "tests" => "Validation plan: unit tests for domain rules and workflow gates; integration and restart tests remain required.",
            "documentation" => "Document API contracts, decisions, setup, assumptions, risks, and operational limits.",
            "security_review" => "Review URL validation, bounded retries, approval identity, sensitive-data handling, and storage permissions.",
            "release_readiness" => "Dependencies are present. Human release approval is required; this prototype never deploys.",
            _ => throw new InvalidOperationException($"Unknown workflow stage '{stageId}'.")
        };
        return Task.FromResult(new StageExecutionResult(output));
    }
}