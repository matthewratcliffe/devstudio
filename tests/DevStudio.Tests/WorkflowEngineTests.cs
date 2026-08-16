using System.Collections.Concurrent;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Sessions;
using DevStudio.Application.Workflows;
using DevStudio.Domain.Common;
using DevStudio.Domain.Sessions;
using DevStudio.Domain.Workflows;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevStudio.Tests;

public class WorkflowEngineTests
{
    [Fact]
    public async Task Every_step_output_is_readable_by_every_later_step()
    {
        var workflows = new InMemoryStore<Workflow>();
        var runs = new InMemoryStore<WorkflowRun>();
        var sessions = new EchoSessionManager();

        var workflow = await workflows.UpsertAsync(new Workflow
        {
            Name = "Chain",
            Inputs = [new WorkflowInput { Name = "task", Required = true }],
            Steps =
            [
                new WorkflowStep { Name = "First", Order = 1, AgentId = "a", PromptTemplate = "do {{task}}" },
                new WorkflowStep { Name = "Second", Order = 2, AgentId = "a", PromptTemplate = "after {{previous}}" },
                // Reaches back past the immediately preceding step.
                new WorkflowStep { Name = "Third", Order = 3, AgentId = "a", PromptTemplate = "recall {{steps.first}} and {{steps.second}}" },
            ],
        });

        var engine = new WorkflowEngine(workflows, runs, sessions, NullLogger<WorkflowEngine>.Instance);

        var run = await engine.RunAsync(workflow.Id, new Dictionary<string, string> { ["task"] = "the job" }, "test");

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal("do the job", run.Steps[0].Output);
        Assert.Equal("after do the job", run.Steps[1].Output);
        Assert.Equal("recall do the job and after do the job", run.Steps[2].Output);
    }

    [Fact]
    public async Task Steps_sharing_an_order_run_at_the_same_time()
    {
        var workflows = new InMemoryStore<Workflow>();
        var runs = new InMemoryStore<WorkflowRun>();
        var sessions = new EchoSessionManager { Delay = TimeSpan.FromMilliseconds(200) };

        var workflow = await workflows.UpsertAsync(new Workflow
        {
            Name = "Fan out",
            Steps =
            [
                new WorkflowStep { Name = "A", Order = 1, AgentId = "a", PromptTemplate = "a" },
                new WorkflowStep { Name = "B", Order = 1, AgentId = "a", PromptTemplate = "b" },
                new WorkflowStep { Name = "C", Order = 1, AgentId = "a", PromptTemplate = "c" },
            ],
        });

        var engine = new WorkflowEngine(workflows, runs, sessions, NullLogger<WorkflowEngine>.Instance);

        var run = await engine.RunAsync(workflow.Id, new Dictionary<string, string>(), "test");

        Assert.Equal(RunStatus.Succeeded, run.Status);
        // If the steps ran sequentially, no more than one would ever be in flight at once.
        Assert.Equal(3, sessions.MaxConcurrent);
    }

    [Fact]
    public async Task A_failing_step_stops_the_run_unless_it_is_allowed_to_continue()
    {
        var workflows = new InMemoryStore<Workflow>();
        var runs = new InMemoryStore<WorkflowRun>();
        var sessions = new EchoSessionManager { FailPrompts = { "boom" } };

        var workflow = await workflows.UpsertAsync(new Workflow
        {
            Name = "Failing",
            Steps =
            [
                new WorkflowStep { Name = "Break", Order = 1, AgentId = "a", PromptTemplate = "boom" },
                new WorkflowStep { Name = "Never", Order = 2, AgentId = "a", PromptTemplate = "unreachable" },
            ],
        });

        var engine = new WorkflowEngine(workflows, runs, sessions, NullLogger<WorkflowEngine>.Instance);

        var run = await engine.RunAsync(workflow.Id, new Dictionary<string, string>(), "test");

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Equal(RunStatus.Pending, run.Steps[1].Status);
    }

    [Fact]
    public async Task A_missing_required_input_is_rejected_before_anything_runs()
    {
        var workflows = new InMemoryStore<Workflow>();
        var runs = new InMemoryStore<WorkflowRun>();
        var sessions = new EchoSessionManager();

        var workflow = await workflows.UpsertAsync(new Workflow
        {
            Name = "Needs input",
            Inputs = [new WorkflowInput { Name = "task", Required = true }],
            Steps = [new WorkflowStep { Name = "Only", Order = 1, AgentId = "a", PromptTemplate = "{{task}}" }],
        });

        var engine = new WorkflowEngine(workflows, runs, sessions, NullLogger<WorkflowEngine>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.RunAsync(workflow.Id, new Dictionary<string, string>(), "test"));

        Assert.Empty(sessions.Prompts);
    }

    /// <summary>Stands in for the JSON store without touching disk.</summary>
    private sealed class InMemoryStore<T> : IEntityStore<T> where T : class, IEntity
    {
        private readonly ConcurrentDictionary<string, T> _items = new();

        public event Action<T>? Changed;

        public Task<IReadOnlyList<T>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<T>>(_items.Values.ToList());

        public Task<T?> GetAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(_items.TryGetValue(id, out var item) ? item : null);

        public Task<T> UpsertAsync(T entity, CancellationToken ct = default)
        {
            _items[entity.Id] = entity;
            Changed?.Invoke(entity);
            return Task.FromResult(entity);
        }

        public Task<bool> DeleteAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(_items.TryRemove(id, out _));
    }

    /// <summary>Returns the prompt it was given as the agent's answer, so substitution is observable.</summary>
    private sealed class EchoSessionManager : ISessionManager
    {
        public List<string> Prompts { get; } = [];
        public HashSet<string> FailPrompts { get; } = [];
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;

        public IReadOnlyList<ChatSession> Live => [];

        public event Action<ChatSession>? SessionUpdated;

        private int _inFlight;
        public int MaxConcurrent { get; private set; }

        public async Task<ChatSession> StartAsync(StartSessionRequest request, CancellationToken ct = default)
        {
            lock (Prompts)
                Prompts.Add(request.Prompt);

            var inFlight = Interlocked.Increment(ref _inFlight);
            lock (Prompts)
                MaxConcurrent = Math.Max(MaxConcurrent, inFlight);

            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, ct);

            Interlocked.Decrement(ref _inFlight);

            var failed = FailPrompts.Contains(request.Prompt);
            var session = new ChatSession
            {
                AgentId = request.AgentId,
                Title = request.Title ?? request.Prompt,
                Status = failed ? SessionStatus.Failed : SessionStatus.AwaitingInput,
                LastError = failed ? "exploded" : null,
                WorkingDirectory = "/tmp/echo",
                Messages = [new ChatMessage { Role = MessageRole.Agent, Content = request.Prompt }],
            };

            SessionUpdated?.Invoke(session);
            return session;
        }

        public Task<ChatSession> RunToCompletionAsync(StartSessionRequest request, TimeSpan timeout, CancellationToken ct = default) =>
            StartAsync(request, ct);

        public Task SendAsync(string sessionId, string message, CancellationToken ct = default) => Task.CompletedTask;

        public Task<GuidanceMessage> SendGuidanceAsync(
            string sessionId,
            string guidance,
            string source = "operator",
            bool interrupt = false,
            CancellationToken ct = default) =>
            Task.FromResult(new GuidanceMessage { Text = guidance, Source = source });

        public Task<IReadOnlyList<GuidanceMessage>> TakeGuidanceAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GuidanceMessage>>([]);

        public Task<ToolApproval?> ResolveApprovalAsync(string sessionId, string approvalId, bool allow, CancellationToken ct = default) =>
            Task.FromResult<ToolApproval?>(null);

        public Task CancelAsync(string sessionId) => Task.CompletedTask;

        public Task<ChatSession?> SetStatusAsync(
            string sessionId,
            SessionStatus status,
            CancellationToken ct = default) => Task.FromResult<ChatSession?>(null);

        public Task<ChatSession?> CloseAsync(string sessionId, string? reason = null, CancellationToken ct = default) =>
            Task.FromResult<ChatSession?>(null);

        public Task<ChatSession?> GetAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult<ChatSession?>(null);

        public Task<IReadOnlyList<ChatSession>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ChatSession>>([]);

        public Task<bool> DeleteAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(true);
    }
}
