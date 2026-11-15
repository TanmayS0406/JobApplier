using JobApplier.Agents;
using Microsoft.Agents.AI.Workflows;

namespace JobApplier.Tests;

/// <summary>
/// Pins down how the Agent Framework workflow engine routes messages, using fake executors (no Claude involved).
/// <para>
/// The engine silently drops a message whose type matches no executor's input, so a chain whose stages return a
/// <see cref="StageResult"/> but accept something else just stops after the first stage. The real pipeline hit exactly that.
/// </para>
/// </summary>
public class WorkflowSemanticsTests
{
    /// <summary>Same shape as the real stages: consumes the previous stage's result, returns its own.</summary>
    private sealed class Step : Executor<StageResult, StageResult>
    {
        private readonly List<string> _trace;
        private readonly bool _continue;

        public Step(string id, List<string> trace, bool @continue = true) : base(id)
        {
            _trace = trace;
            _continue = @continue;
        }

        public override async ValueTask<StageResult> HandleAsync(StageResult previous, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            await Task.Delay(20, cancellationToken);
            lock (_trace) _trace.Add(Id);
            return new StageResult(previous.JobId, _continue);
        }
    }

    /// <summary>The mistake: accepts a Guid although the previous stage hands over a StageResult.</summary>
    private sealed class WrongInputStep : Executor<Guid, StageResult>
    {
        private readonly List<string> _trace;

        public WrongInputStep(string id, List<string> trace) : base(id) => _trace = trace;

        public override ValueTask<StageResult> HandleAsync(Guid jobId, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            lock (_trace) _trace.Add(Id);
            return new(new StageResult(jobId, true));
        }
    }

    private static async Task<List<string>> RunAsync(Func<List<string>, Workflow> build)
    {
        var trace = new List<string>();
        await using var run = await InProcessExecution.RunStreamingAsync(build(trace), new StageResult(Guid.NewGuid(), true));
        await foreach (var _ in run.WatchStreamAsync()) { /* drain */ }
        return trace;
    }

    [Fact]
    public async Task Conditional_edges_run_every_stage_when_each_one_continues()
    {
        var trace = await RunAsync(t =>
        {
            var a = new Step("a", t); var b = new Step("b", t); var c = new Step("c", t);
            return new WorkflowBuilder(a)
                .AddEdge<StageResult>(a, b, r => r is { Continue: true })
                .AddEdge<StageResult>(b, c, r => r is { Continue: true })
                .WithOutputFrom(c)
                .Build();
        });

        Assert.Equal(new[] { "a", "b", "c" }, trace);
    }

    [Fact]
    public async Task A_stage_that_does_not_continue_ends_the_pipeline()
    {
        var trace = await RunAsync(t =>
        {
            var a = new Step("a", t); var b = new Step("b", t, @continue: false); var c = new Step("c", t);
            return new WorkflowBuilder(a)
                .AddEdge<StageResult>(a, b, r => r is { Continue: true })
                .AddEdge<StageResult>(b, c, r => r is { Continue: true })
                .WithOutputFrom(c)
                .Build();
        });

        Assert.Equal(new[] { "a", "b" }, trace);
    }

    [Fact]
    public async Task Documents_the_trap_a_stage_with_the_wrong_input_type_is_silently_skipped()
    {
        var trace = await RunAsync(t =>
        {
            var a = new Step("a", t); var b = new WrongInputStep("b", t);
            return new WorkflowBuilder(a).AddEdge<StageResult>(a, b, r => r is { Continue: true }).WithOutputFrom(b).Build();
        });

        Assert.Equal(new[] { "a" }, trace); // 'b' never ran, and nothing complained
    }

    /// <summary>Guards the real pipeline against the same mistake: every stage must consume what the previous one produces.</summary>
    [Theory]
    [InlineData(typeof(ExtractExecutor))]
    [InlineData(typeof(FindExecutor))]
    [InlineData(typeof(MatchExecutor))]
    [InlineData(typeof(ApplyExecutor))]
    public void Every_real_stage_consumes_and_returns_a_StageResult(Type stage) =>
        Assert.True(typeof(Executor<StageResult, StageResult>).IsAssignableFrom(stage));
}
