using AgenticUrlShortener.Api.Workflows;

namespace AgenticUrlShortener.Tests;

public sealed class WorkflowOrchestratorTests
{
    [Fact]
    public async Task GreenfieldWorkflow_RunsParallelWorkAndRequiresReleaseApproval()
    {
        var orchestrator = new WorkflowOrchestrator(new RuleBasedStageAgent());
        var run = await StartAsync(orchestrator, "greenfield", "Build a URL shortener with analytics.");

        Assert.Equal(WorkflowRunStatus.AwaitingApproval, run.Status);
        Assert.Equal(WorkflowStageStatus.Succeeded, Stage(run, "tests").Status);
        Assert.Equal(WorkflowStageStatus.Succeeded, Stage(run, "documentation").Status);
        Assert.Equal(WorkflowStageStatus.Succeeded, Stage(run, "security_review").Status);
        Assert.Equal(WorkflowStageStatus.WaitingApproval, Stage(run, "release_approval").Status);

        run = await ApproveAsync(orchestrator, run.Id, "Checks passed.");

        Assert.Equal(WorkflowRunStatus.Succeeded, run.Status);
        Assert.Contains(run.Decisions, decision => decision.Decision == "approved" && decision.StageId == "release_approval");
        Assert.Equal(1, (await orchestrator.GetMetricsAsync()).SuccessRate);
    }

    [Fact]
    public async Task AmbiguousWorkflow_RequiresClarificationBeforeArchitecture()
    {
        var orchestrator = new WorkflowOrchestrator(new RuleBasedStageAgent());
        var run = await StartAsync(orchestrator, "ambiguous", "Make links fast and secure.");

        Assert.Equal(WorkflowRunStatus.AwaitingApproval, run.Status);
        Assert.Equal(WorkflowStageStatus.WaitingApproval, Stage(run, "clarification_approval").Status);
        Assert.Equal(WorkflowStageStatus.Pending, Stage(run, "architecture").Status);

        run = await ApproveAsync(orchestrator, run.Id, "Assume 1M links and 10K redirects per second.");

        Assert.Equal(WorkflowStageStatus.Succeeded, Stage(run, "architecture").Status);
        Assert.Equal(WorkflowStageStatus.WaitingApproval, Stage(run, "release_approval").Status);
        Assert.Contains(run.Decisions, decision => decision.StageId == "clarification_approval");
    }

    [Fact]
    public async Task TransientAgentFailure_IsRetriedWithinBound()
    {
        var agent = new ControlledAgent("architecture", failuresBeforeSuccess: 1);
        var orchestrator = new WorkflowOrchestrator(agent);
        var run = await StartAsync(orchestrator, "brownfield", "Add analytics to current links.");

        Assert.Equal(2, Stage(run, "architecture").Attempts);
        Assert.Equal(1, (await orchestrator.GetMetricsAsync()).RetryCount);
        Assert.Contains("existing API", Stage(run, "understand").Output);
    }

    [Fact]
    public async Task FailedDocumentation_UsesVisibleFallbackAndContinues()
    {
        var orchestrator = new WorkflowOrchestrator(new ControlledAgent("documentation", failuresBeforeSuccess: int.MaxValue));
        var run = await StartAsync(orchestrator, "greenfield", "Create a redirect API.");

        Assert.True(Stage(run, "documentation").UsedFallback);
        Assert.Equal(3, Stage(run, "documentation").Attempts);
        Assert.Equal(WorkflowStageStatus.Succeeded, Stage(run, "documentation").Status);
        Assert.Contains(run.Audit, item => item.Action == "stage.fallback");
    }

    [Fact]
    public async Task Replan_InvalidatesPriorOutputsAndIncrementsLineage()
    {
        var orchestrator = new WorkflowOrchestrator(new RuleBasedStageAgent());
        var run = await StartAsync(orchestrator, "greenfield", "Create redirects.");
        run = await orchestrator.ReplanAsync(run.Id, new ReplanRequest("Also retain daily click analytics.", "New retention requirement."), "analyst", CancellationToken.None);

        Assert.Equal(2, run.PlanRevision);
        Assert.Equal("Also retain daily click analytics.", run.Requirement);
        Assert.Equal(WorkflowStageStatus.Succeeded, Stage(run, "understand").Status);
        Assert.Equal(2, run.Audit.Last().PlanRevision);
        Assert.Contains(run.Decisions, decision => decision.Decision == "replanned");
    }

    [Fact]
    public async Task SafeStopCanResumeFromCompletedStages()
    {
        var orchestrator = new WorkflowOrchestrator(new RuleBasedStageAgent());
        var run = await StartAsync(orchestrator, "greenfield", "Create redirects.");
        run = await orchestrator.SafeStopAsync(run.Id, "operator");
        Assert.Equal(WorkflowRunStatus.SafeStopped, run.Status);

        run = await orchestrator.ResumeAsync(run.Id, "operator", CancellationToken.None);

        Assert.Equal(WorkflowRunStatus.AwaitingApproval, run.Status);
        Assert.Equal(WorkflowStageStatus.Succeeded, Stage(run, "release_readiness").Status);
    }

    [Fact]
    public async Task SafeStoppedWorkflow_CannotAcceptAnApproval()
    {
        var orchestrator = new WorkflowOrchestrator(new RuleBasedStageAgent());
        var run = await StartAsync(orchestrator, "greenfield", "Create redirects.");
        await orchestrator.SafeStopAsync(run.Id, "operator");

        await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.ApproveAsync(run.Id, new ApprovalRequest("Approve"), "operator", CancellationToken.None));
    }

    [Fact]
    public async Task WorkflowReads_ReturnIndependentSnapshots()
    {
        var orchestrator = new WorkflowOrchestrator(new RuleBasedStageAgent());
        var run = await StartAsync(orchestrator, "greenfield", "Create redirects.");
        var snapshot = await orchestrator.GetAsync(run.Id);
        snapshot!.Requirement = "tampered snapshot";
        Stage(snapshot, "understand").Output = "tampered output";

        var actual = await orchestrator.GetAsync(run.Id);

        Assert.Equal("Create redirects.", actual!.Requirement);
        Assert.Contains("Create redirects.", Stage(actual, "understand").Output);
    }

    [Fact]
    public async Task Rollback_RecordsReasonAndIncrementsReliabilityMetric()
    {
        var orchestrator = new WorkflowOrchestrator(new RuleBasedStageAgent());
        var run = await StartAsync(orchestrator, "greenfield", "Create redirects.");
        run = await orchestrator.RollbackAsync(run.Id, new RollbackRequest("Review found a policy gap."), "operator");

        Assert.Equal(WorkflowRunStatus.RolledBack, run.Status);
        Assert.Contains(run.Audit, item => item.Action == "run.rolled_back" && item.Details.Contains("policy gap"));
        Assert.Equal(1, (await orchestrator.GetMetricsAsync()).RollbackCount);
    }

    private static WorkflowStage Stage(WorkflowRun run, string id) => run.Stages.Single(stage => stage.Id == id);
    private static Task<WorkflowRun> StartAsync(WorkflowOrchestrator orchestrator, string scenario, string requirement) =>
        orchestrator.StartAsync(new StartWorkflowRequest(scenario, requirement), "test-subject", CancellationToken.None);
    private static Task<WorkflowRun> ApproveAsync(WorkflowOrchestrator orchestrator, string id, string note) =>
        orchestrator.ApproveAsync(id, new ApprovalRequest(note), "test-reviewer", CancellationToken.None);

    private sealed class ControlledAgent(string failingStage, int failuresBeforeSuccess) : IStageAgent
    {
        private int _failures;

        public async Task<StageExecutionResult> ExecuteAsync(string stageId, StageExecutionContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stageId == failingStage && Interlocked.Increment(ref _failures) <= failuresBeforeSuccess)
                throw new InvalidOperationException("Injected transient failure.");
            return await new RuleBasedStageAgent().ExecuteAsync(stageId, context, cancellationToken);
        }
    }
}