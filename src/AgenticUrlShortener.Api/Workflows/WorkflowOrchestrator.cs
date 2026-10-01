using System.Collections.Concurrent;

namespace AgenticUrlShortener.Api.Workflows;

public sealed class WorkflowOrchestrator(IStageAgent agent)
{
    private const int MaximumAttempts = 3;
    private const int MaximumParallelStages = 3;
    private readonly ConcurrentDictionary<string, RunState> _runs = new(StringComparer.Ordinal);
    private int _retryCount;
    private int _rollbackCount;

    public async Task<WorkflowRun> StartAsync(StartWorkflowRequest request, string actor, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<WorkflowScenario>(request.Scenario, true, out var scenario)) throw new ArgumentException("Scenario must be greenfield, brownfield, or ambiguous.");
        ValidateText(request.Requirement, nameof(request.Requirement));
        ValidateText(actor, nameof(actor));
        var run = new WorkflowRun { Id = Guid.NewGuid().ToString("N"), Scenario = scenario, Requirement = request.Requirement.Trim(), CreatedAt = DateTimeOffset.UtcNow };
        AddStage(run, "understand", "Requirement understanding", []);
        if (scenario == WorkflowScenario.Ambiguous) AddStage(run, "clarification_approval", "Confirm assumptions", ["understand"], true);
        AddStage(run, "architecture", "Architecture and task plan", [scenario == WorkflowScenario.Ambiguous ? "clarification_approval" : "understand"]);
        AddStage(run, "implementation", "Implementation", ["architecture"]);
        AddStage(run, "tests", "Automated validation", ["implementation"]);
        AddStage(run, "documentation", "Documentation", ["architecture"]);
        AddStage(run, "security_review", "Security and policy review", ["implementation"]);
        AddStage(run, "release_readiness", "Release readiness", ["tests", "documentation", "security_review"]);
        AddStage(run, "release_approval", "Human release approval", ["release_readiness"], true);
        run.Audit.Add(new WorkflowAuditEvent(DateTimeOffset.UtcNow, "run.created", actor.Trim(), $"Scenario={scenario}; requirement accepted.", run.PlanRevision));
        var state = new RunState(run);
        if (!_runs.TryAdd(run.Id, state)) throw new InvalidOperationException("Could not allocate a workflow run identifier.");
        await AdvanceAsync(state, CancellationToken.None);
        return await GetAsync(run.Id, cancellationToken) ?? run;
    }

    public async Task<WorkflowRun?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!_runs.TryGetValue(id, out var state)) return null;
        return await SnapshotAsync(state, cancellationToken);
    }

    public async Task<IReadOnlyList<WorkflowRun>> ListAsync(CancellationToken cancellationToken = default)
    {
        var snapshots = new List<WorkflowRun>();
        foreach (var state in _runs.Values) snapshots.Add(await SnapshotAsync(state, cancellationToken));
        return snapshots.OrderByDescending(run => run.CreatedAt).ToArray();
    }

    public async Task<WorkflowRun> ApproveAsync(string id, ApprovalRequest request, string actor, CancellationToken cancellationToken)
    {
        ValidateText(actor, nameof(actor));
        ValidateText(request.Note, nameof(request.Note));
        var state = Find(id);
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            var run = state.Run;
            if (run.Status != WorkflowRunStatus.AwaitingApproval || run.StopRequested)
                throw new InvalidOperationException("Approvals are only accepted for an active workflow approval gate.");
            var gate = run.Stages.FirstOrDefault(stage => stage.Status == WorkflowStageStatus.WaitingApproval) ?? throw new InvalidOperationException("This workflow has no pending approval.");
            gate.Status = WorkflowStageStatus.Succeeded;
            gate.Output = $"Approved by {actor.Trim()}: {request.Note.Trim()}";
            gate.CompletedAt = DateTimeOffset.UtcNow;
            run.Decisions.Add(new WorkflowDecision(DateTimeOffset.UtcNow, gate.Id, actor.Trim(), "approved", request.Note.Trim(), run.PlanRevision));
            run.Audit.Add(new WorkflowAuditEvent(DateTimeOffset.UtcNow, "approval.granted", actor.Trim(), $"Stage={gate.Id}; {request.Note.Trim()}", run.PlanRevision));
            run.Status = WorkflowRunStatus.Running;
            run.CompletedAt = null;
        }
        finally { state.Gate.Release(); }
        await AdvanceAsync(state, CancellationToken.None);
        return await SnapshotAsync(state, cancellationToken);
    }

    public async Task<WorkflowRun> ReplanAsync(string id, ReplanRequest request, string actor, CancellationToken cancellationToken)
    {
        ValidateText(request.Requirement, nameof(request.Requirement));
        ValidateText(actor, nameof(actor));
        ValidateText(request.Reason, nameof(request.Reason));
        var state = Find(id);
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            var run = state.Run;
            if (run.Status == WorkflowRunStatus.RolledBack) throw new InvalidOperationException("A rolled-back run cannot be replanned; start a new run.");
            if (run.FailedAt is { } failedAt) run.RecoveryDurationsMs.Add((DateTimeOffset.UtcNow - failedAt).TotalMilliseconds);
            run.PlanRevision++;
            run.Requirement = request.Requirement.Trim();
            run.Status = WorkflowRunStatus.Running;
            run.StopRequested = false;
            run.CompletedAt = null;
            run.FailedAt = null;
            foreach (var stage in run.Stages)
            {
                stage.Status = WorkflowStageStatus.Pending;
                stage.Attempts = 0;
                stage.Output = null;
                stage.Error = null;
                stage.UsedFallback = false;
                stage.StartedAt = null;
                stage.CompletedAt = null;
            }
            run.Audit.Add(new WorkflowAuditEvent(DateTimeOffset.UtcNow, "plan.revised", actor.Trim(), request.Reason.Trim(), run.PlanRevision));
            run.Decisions.Add(new WorkflowDecision(DateTimeOffset.UtcNow, "understand", actor.Trim(), "replanned", request.Reason.Trim(), run.PlanRevision));
            state.ReplaceStopToken();
        }
        finally { state.Gate.Release(); }
        await AdvanceAsync(state, CancellationToken.None);
        return await SnapshotAsync(state, cancellationToken);
    }

    public async Task<WorkflowRun> SafeStopAsync(string id, string actor)
    {
        ValidateText(actor, nameof(actor));
        var state = Find(id);
        state.Run.StopRequested = true;
        state.StopSource.Cancel();
        await state.Gate.WaitAsync();
        try
        {
            if (state.Run.Status is not (WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed or WorkflowRunStatus.RolledBack))
            {
                state.Run.Status = WorkflowRunStatus.SafeStopped;
                state.Run.CompletedAt = DateTimeOffset.UtcNow;
                state.Run.Audit.Add(new WorkflowAuditEvent(DateTimeOffset.UtcNow, "run.safe_stopped", actor.Trim(), "Execution halted by control request.", state.Run.PlanRevision));
                foreach (var stage in state.Run.Stages.Where(stage => stage.Status == WorkflowStageStatus.Running)) stage.Status = WorkflowStageStatus.Pending;
            }
            return CopyRun(state.Run);
        }
        finally { state.Gate.Release(); }
    }

    public async Task<WorkflowRun> ResumeAsync(string id, string actor, CancellationToken cancellationToken)
    {
        ValidateText(actor, nameof(actor));
        var state = Find(id);
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            if (state.Run.Status != WorkflowRunStatus.SafeStopped) throw new InvalidOperationException("Only a safe-stopped workflow can be resumed.");
            state.Run.StopRequested = false;
            state.Run.Status = WorkflowRunStatus.Running;
            state.Run.CompletedAt = null;
            state.ReplaceStopToken();
            state.Run.Audit.Add(new WorkflowAuditEvent(DateTimeOffset.UtcNow, "run.resumed", actor.Trim(), "Execution resumed from completed stages.", state.Run.PlanRevision));
        }
        finally { state.Gate.Release(); }
        await AdvanceAsync(state, CancellationToken.None);
        return await SnapshotAsync(state, cancellationToken);
    }

    public async Task<WorkflowRun> RollbackAsync(string id, RollbackRequest request, string actor)
    {
        ValidateText(actor, nameof(actor));
        ValidateText(request.Reason, nameof(request.Reason));
        var state = Find(id);
        state.Run.StopRequested = true;
        state.StopSource.Cancel();
        await state.Gate.WaitAsync();
        try
        {
            var run = state.Run;
            if (run.Status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.RolledBack) throw new InvalidOperationException("This workflow cannot be rolled back from its current state.");
            foreach (var stage in run.Stages.Where(stage => stage.Status == WorkflowStageStatus.Succeeded))
            {
                stage.Status = WorkflowStageStatus.Skipped;
                stage.Output = null;
            }
            run.Status = WorkflowRunStatus.RolledBack;
            run.CompletedAt = DateTimeOffset.UtcNow;
            run.Audit.Add(new WorkflowAuditEvent(DateTimeOffset.UtcNow, "run.rolled_back", actor.Trim(), request.Reason.Trim(), run.PlanRevision));
            run.Decisions.Add(new WorkflowDecision(DateTimeOffset.UtcNow, "workflow", actor.Trim(), "rolled_back", request.Reason.Trim(), run.PlanRevision));
            Interlocked.Increment(ref _rollbackCount);
            return CopyRun(run);
        }
        finally { state.Gate.Release(); }
    }

    public async Task<WorkflowMetrics> GetMetricsAsync(CancellationToken cancellationToken = default)
    {
        var runs = await ListAsync(cancellationToken);
        var terminal = runs.Where(run => run.Status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed or WorkflowRunStatus.RolledBack).ToArray();
        var recovery = runs.SelectMany(run => run.RecoveryDurationsMs).ToArray();
        var latencies = terminal.Where(run => run.CompletedAt.HasValue).Select(run => (run.CompletedAt!.Value - run.CreatedAt).TotalMilliseconds).ToArray();
        var successes = terminal.Count(run => run.Status == WorkflowRunStatus.Succeeded);
        return new WorkflowMetrics(runs.Count, terminal.Length, terminal.Length == 0 ? 0 : (double)successes / terminal.Length,
            Volatile.Read(ref _retryCount), Volatile.Read(ref _rollbackCount), recovery.Length == 0 ? null : recovery.Average(), latencies.Length == 0 ? null : latencies.Average());
    }

    private async Task AdvanceAsync(RunState state, CancellationToken cancellationToken)
    {
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            var run = state.Run;
            if (run.Status is WorkflowRunStatus.SafeStopped or WorkflowRunStatus.RolledBack or WorkflowRunStatus.Failed) return;
            using var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, state.StopSource.Token);
            var token = linkedToken.Token;
            using var parallelism = new SemaphoreSlim(MaximumParallelStages, MaximumParallelStages);
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var ready = run.Stages.Where(candidate => candidate.Status == WorkflowStageStatus.Pending &&
                        candidate.DependsOn.All(dependency => run.Stages.Single(item => item.Id == dependency).Status == WorkflowStageStatus.Succeeded)).ToArray();
                    if (ready.Length == 0) break;
                    var gate = ready.FirstOrDefault(candidate => candidate.RequiresApproval);
                    if (gate is not null)
                    {
                        gate.Status = WorkflowStageStatus.WaitingApproval;
                        run.Status = WorkflowRunStatus.AwaitingApproval;
                        run.Audit.Add(new WorkflowAuditEvent(DateTimeOffset.UtcNow, "approval.required", "orchestrator", $"Stage={gate.Id}", run.PlanRevision));
                        return;
                    }

                    var outputs = run.Stages.Where(item => item.Status == WorkflowStageStatus.Succeeded && item.Output is not null)
                        .ToDictionary(item => item.Id, item => item.Output!, StringComparer.Ordinal);
                    var context = new StageExecutionContext(run.Scenario, run.Requirement, outputs);
                    foreach (var stage in ready)
                    {
                        stage.Status = WorkflowStageStatus.Running;
                        stage.StartedAt = DateTimeOffset.UtcNow;
                    }
                    var results = await Task.WhenAll(ready.Select(stage => ExecuteBoundedAsync(stage.Id, context, parallelism, token)));
                    token.ThrowIfCancellationRequested();
                    for (var index = 0; index < ready.Length; index++)
                    {
                        var stage = ready[index];
                        var result = results[index];
                        stage.Attempts = result.Attempts;
                        stage.CompletedAt = DateTimeOffset.UtcNow;
                        stage.UsedFallback = result.UsedFallback;
                        if (result.Error is not null)
                        {
                            stage.Status = WorkflowStageStatus.Failed;
                            stage.Error = result.Error;
                            run.Status = WorkflowRunStatus.Failed;
                            run.FailedAt = run.CompletedAt = DateTimeOffset.UtcNow;
                            run.Audit.Add(new WorkflowAuditEvent(DateTimeOffset.UtcNow, "stage.failed", "orchestrator", $"Stage={stage.Id}; {result.Error}", run.PlanRevision));
                            return;
                        }
                        stage.Status = WorkflowStageStatus.Succeeded;
                        stage.Output = result.Output;
                        run.Audit.Add(new WorkflowAuditEvent(DateTimeOffset.UtcNow, result.UsedFallback ? "stage.fallback" : "stage.completed", "agent", $"Stage={stage.Id}; attempts={result.Attempts}", run.PlanRevision));
                    }
                }

                if (run.Stages.All(stage => stage.Status == WorkflowStageStatus.Succeeded))
                {
                    run.Status = WorkflowRunStatus.Succeeded;
                    run.CompletedAt = DateTimeOffset.UtcNow;
                    run.Audit.Add(new WorkflowAuditEvent(DateTimeOffset.UtcNow, "run.completed", "orchestrator", "All workflow stages completed.", run.PlanRevision));
                }
                else if (run.Stages.Any(stage => stage.Status == WorkflowStageStatus.WaitingApproval)) run.Status = WorkflowRunStatus.AwaitingApproval;
                else run.Status = WorkflowRunStatus.Running;
            }
            catch (OperationCanceledException) when (run.StopRequested || state.StopSource.IsCancellationRequested)
            {
                run.Status = WorkflowRunStatus.SafeStopped;
                run.CompletedAt = DateTimeOffset.UtcNow;
                foreach (var stage in run.Stages.Where(stage => stage.Status == WorkflowStageStatus.Running)) stage.Status = WorkflowStageStatus.Pending;
            }
        }
        finally { state.Gate.Release(); }
    }

    private async Task<BoundedExecution> ExecuteBoundedAsync(string stageId, StageExecutionContext context, SemaphoreSlim parallelism, CancellationToken cancellationToken)
    {
        await parallelism.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
            {
                try
                {
                    var result = await agent.ExecuteAsync(stageId, context, cancellationToken);
                    if (attempt > 1) Interlocked.Add(ref _retryCount, attempt - 1);
                    return new BoundedExecution(result.Output, attempt, false, null);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception) when (attempt < MaximumAttempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken);
                }
                catch (Exception exception)
                {
                    if (attempt > 1) Interlocked.Add(ref _retryCount, attempt - 1);
                    if (stageId == "documentation") return new BoundedExecution("Fallback documentation generated from accepted requirement and workflow metadata.", attempt, true, null);
                    return new BoundedExecution(null, attempt, false, $"Agent failed after {MaximumAttempts} attempts: {exception.Message}");
                }
            }
            throw new InvalidOperationException("Bounded execution exited unexpectedly.");
        }
        finally { parallelism.Release(); }
    }

    private RunState Find(string id) => _runs.TryGetValue(id, out var state) ? state : throw new KeyNotFoundException();
    private static async Task<WorkflowRun> SnapshotAsync(RunState state, CancellationToken cancellationToken)
    {
        await state.Gate.WaitAsync(cancellationToken);
        try { return CopyRun(state.Run); }
        finally { state.Gate.Release(); }
    }

    private static WorkflowRun CopyRun(WorkflowRun run)
    {
        var copy = new WorkflowRun
        {
            Id = run.Id,
            Scenario = run.Scenario,
            Requirement = run.Requirement,
            CreatedAt = run.CreatedAt,
            Status = run.Status,
            PlanRevision = run.PlanRevision,
            StopRequested = run.StopRequested,
            CompletedAt = run.CompletedAt,
            FailedAt = run.FailedAt
        };
        copy.RecoveryDurationsMs.AddRange(run.RecoveryDurationsMs);
        foreach (var stage in run.Stages)
        {
            var stageCopy = new WorkflowStage(stage.Id, stage.Name, stage.DependsOn.ToArray(), stage.RequiresApproval)
            {
                Status = stage.Status,
                Attempts = stage.Attempts,
                Output = stage.Output,
                Error = stage.Error,
                UsedFallback = stage.UsedFallback,
                StartedAt = stage.StartedAt,
                CompletedAt = stage.CompletedAt
            };
            copy.Stages.Add(stageCopy);
        }
        copy.Audit.AddRange(run.Audit);
        copy.Decisions.AddRange(run.Decisions);
        return copy;
    }

    private static void AddStage(WorkflowRun run, string id, string name, IReadOnlyList<string> dependencies, bool requiresApproval = false) => run.Stages.Add(new WorkflowStage(id, name, dependencies, requiresApproval));
    private static void ValidateText(string? value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4000) throw new ArgumentException($"{parameter} must contain between 1 and 4000 characters.");
    }

    private sealed record BoundedExecution(string? Output, int Attempts, bool UsedFallback, string? Error);
    private sealed class RunState(WorkflowRun run)
    {
        public WorkflowRun Run { get; } = run;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public CancellationTokenSource StopSource { get; private set; } = new();
        public void ReplaceStopToken()
        {
            StopSource.Dispose();
            StopSource = new CancellationTokenSource();
        }
    }
}