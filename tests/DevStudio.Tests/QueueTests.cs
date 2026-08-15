using System.Collections.Concurrent;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Application.Queues;
using DevStudio.Application.Sessions;
using DevStudio.Application.Workflows;
using DevStudio.Domain.Common;
using DevStudio.Domain.Queues;
using DevStudio.Domain.Sessions;
using DevStudio.Domain.Workflows;
using DevStudio.Infrastructure.Queues;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevStudio.Tests;

public class QueueTests
{
    [Fact]
    public async Task The_same_key_is_only_queued_once_while_it_is_outstanding()
    {
        var (service, _, queue, _) = Build(q => q.Dedupe = QueueDedupe.Active);

        var first = await service.EnqueueAsync(Item(queue, "mr-42"));
        var second = await service.EnqueueAsync(Item(queue, "mr-42"));

        Assert.True(first.Accepted);
        Assert.False(second.Accepted);
        Assert.Equal(first.Item!.Id, second.Item!.Id);
        Assert.Single(await service.GetItemsAsync(queue.Id));
    }

    [Fact]
    public async Task A_finished_key_can_be_queued_again_when_deduplication_is_active_only()
    {
        var (service, _, queue, _) = Build(q => q.Dedupe = QueueDedupe.Active);

        var first = await service.EnqueueAsync(Item(queue, "mr-42"));
        await service.ClaimAsync(queue.Id, 1);
        await service.CompleteAsync(first.Item!, QueueOutcome.Success("done"));

        // The merge request was closed and reopened: this is new work, not a duplicate.
        var again = await service.EnqueueAsync(Item(queue, "mr-42"));

        Assert.True(again.Accepted);
        Assert.Equal(2, (await service.GetItemsAsync(queue.Id)).Count);
    }

    [Fact]
    public async Task A_key_queued_ever_is_refused_for_good()
    {
        var (service, _, queue, _) = Build(q => q.Dedupe = QueueDedupe.Ever);

        var first = await service.EnqueueAsync(Item(queue, "mr-42"));
        await service.ClaimAsync(queue.Id, 1);
        await service.CompleteAsync(first.Item!, QueueOutcome.Success("done"));

        Assert.False((await service.EnqueueAsync(Item(queue, "mr-42"))).Accepted);
    }

    [Fact]
    public async Task Nothing_is_refused_when_deduplication_is_off()
    {
        var (service, _, queue, _) = Build(q => q.Dedupe = QueueDedupe.None);

        Assert.True((await service.EnqueueAsync(Item(queue, "mr-42"))).Accepted);
        Assert.True((await service.EnqueueAsync(Item(queue, "mr-42"))).Accepted);
    }

    [Fact]
    public async Task An_item_with_no_key_is_never_treated_as_a_duplicate()
    {
        var (service, _, queue, _) = Build(q => q.Dedupe = QueueDedupe.Ever);

        Assert.True((await service.EnqueueAsync(Item(queue, string.Empty, "one"))).Accepted);
        Assert.True((await service.EnqueueAsync(Item(queue, string.Empty, "two"))).Accepted);
    }

    [Fact]
    public async Task Claiming_takes_the_highest_priority_first_and_the_oldest_within_a_priority()
    {
        var (service, items, queue, _) = Build();

        var low = (await service.EnqueueAsync(Item(queue, "low"))).Item!;
        var oldNormal = (await service.EnqueueAsync(Item(queue, "old"))).Item!;
        var newNormal = (await service.EnqueueAsync(Item(queue, "new"))).Item!;
        var urgent = (await service.EnqueueAsync(Item(queue, "urgent") with { Priority = 10 })).Item!;

        low.Priority = -5;
        await items.UpsertAsync(low);

        // Same priority, so the order between these two has to come from when they arrived.
        oldNormal.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await items.UpsertAsync(oldNormal);
        newNormal.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await items.UpsertAsync(newNormal);

        var claimed = await service.ClaimAsync(queue.Id, 3);

        Assert.Equal(["urgent", "old", "new"], claimed.Select(i => i.Key));
    }

    [Fact]
    public async Task Claiming_never_hands_the_same_item_to_two_callers()
    {
        var (service, _, queue, _) = Build();

        for (var i = 0; i < 20; i++)
            await service.EnqueueAsync(Item(queue, $"item-{i}"));

        var claims = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => service.ClaimAsync(queue.Id, 5))));

        var ids = claims.SelectMany(c => c).Select(i => i.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(20, ids.Count);
    }

    [Fact]
    public async Task Claiming_marks_the_item_running_and_counts_the_try()
    {
        var (service, _, queue, _) = Build();
        await service.EnqueueAsync(Item(queue, "mr-42"));

        var claimed = Assert.Single(await service.ClaimAsync(queue.Id, 5));

        Assert.Equal(QueueItemStatus.Running, claimed.Status);
        Assert.Equal(1, claimed.Attempts);
        Assert.Empty(await service.ClaimAsync(queue.Id, 5));
    }

    [Fact]
    public async Task A_failure_with_tries_left_goes_back_to_pending_behind_the_retry_delay()
    {
        var (service, _, queue, _) = Build(q =>
        {
            q.MaxAttempts = 2;
            q.RetryDelayMinutes = 15;
        });

        await service.EnqueueAsync(Item(queue, "mr-42"));
        var claimed = Assert.Single(await service.ClaimAsync(queue.Id, 1));

        await service.CompleteAsync(claimed, QueueOutcome.Failure("the agent fell over"));

        var stored = Assert.Single(await service.GetItemsAsync(queue.Id));
        Assert.Equal(QueueItemStatus.Pending, stored.Status);
        Assert.Equal("the agent fell over", stored.LastError);
        Assert.NotNull(stored.AvailableAt);
        Assert.True(stored.AvailableAt > DateTimeOffset.UtcNow.AddMinutes(14));

        // Held back until the delay has passed, so a failing item does not spin.
        Assert.Empty(await service.ClaimAsync(queue.Id, 1));
    }

    [Fact]
    public async Task A_failure_on_the_last_try_settles_as_failed()
    {
        var (service, _, queue, _) = Build(q => q.MaxAttempts = 1);

        await service.EnqueueAsync(Item(queue, "mr-42"));
        var claimed = Assert.Single(await service.ClaimAsync(queue.Id, 1));

        await service.CompleteAsync(claimed, QueueOutcome.Failure("no"));

        var stored = Assert.Single(await service.GetItemsAsync(queue.Id));
        Assert.Equal(QueueItemStatus.Failed, stored.Status);
        Assert.NotNull(stored.FinishedAt);
    }

    [Fact]
    public async Task A_cancellation_is_not_undone_by_a_late_result()
    {
        var (service, _, queue, _) = Build();

        await service.EnqueueAsync(Item(queue, "mr-42"));
        var claimed = Assert.Single(await service.ClaimAsync(queue.Id, 1));

        await service.CancelAsync(claimed.Id);
        // The agent was already on its way out when the cancel landed.
        await service.CompleteAsync(claimed, QueueOutcome.Success("finished anyway"));

        var stored = Assert.Single(await service.GetItemsAsync(queue.Id));
        Assert.Equal(QueueItemStatus.Cancelled, stored.Status);
    }

    [Fact]
    public async Task Cancelling_a_running_item_stops_the_session_processing_it()
    {
        var (service, items, queue, sessions) = Build();

        await service.EnqueueAsync(Item(queue, "mr-42"));
        var claimed = Assert.Single(await service.ClaimAsync(queue.Id, 1));
        claimed.SessionId = "session-1";
        await items.UpsertAsync(claimed);

        await service.CancelAsync(claimed.Id);

        Assert.Contains("session-1", sessions.Cancelled);
    }

    [Fact]
    public async Task Items_left_running_by_a_restart_are_released_but_keep_their_try()
    {
        var (service, _, queue, _) = Build(q => q.MaxAttempts = 3);

        await service.EnqueueAsync(Item(queue, "mr-42"));
        await service.ClaimAsync(queue.Id, 1);

        Assert.Equal(1, await service.ReleaseOrphansAsync());

        var stored = Assert.Single(await service.GetItemsAsync(queue.Id));
        Assert.Equal(QueueItemStatus.Pending, stored.Status);
        // The interrupted try still counts, or an item that kills the process loops forever.
        Assert.Equal(1, stored.Attempts);
    }

    [Fact]
    public async Task Retrying_a_failed_item_clears_its_history()
    {
        var (service, _, queue, _) = Build();

        await service.EnqueueAsync(Item(queue, "mr-42"));
        var claimed = Assert.Single(await service.ClaimAsync(queue.Id, 1));
        await service.CompleteAsync(claimed, QueueOutcome.Failure("no"));

        Assert.True(await service.RetryAsync(claimed.Id));

        var stored = Assert.Single(await service.GetItemsAsync(queue.Id));
        Assert.Equal(QueueItemStatus.Pending, stored.Status);
        Assert.Equal(0, stored.Attempts);
        Assert.Null(stored.LastError);
    }

    [Fact]
    public async Task Purging_leaves_the_work_that_has_not_finished()
    {
        var (service, _, queue, _) = Build();

        var done = (await service.EnqueueAsync(Item(queue, "done"))).Item!;
        await service.EnqueueAsync(Item(queue, "waiting"));
        await service.ClaimAsync(queue.Id, 1);
        await service.CompleteAsync(done, QueueOutcome.Success("ok"));

        Assert.Equal(1, await service.PurgeAsync(queue.Id));

        var left = Assert.Single(await service.GetItemsAsync(queue.Id));
        Assert.Equal("waiting", left.Key);
    }

    [Fact]
    public async Task Emptying_the_queue_takes_the_outstanding_work_as_well()
    {
        var (service, _, queue, sessions) = Build();

        var done = (await service.EnqueueAsync(Item(queue, "done"))).Item!;
        await service.EnqueueAsync(Item(queue, "waiting"));
        var running = (await service.EnqueueAsync(Item(queue, "running"))).Item!;

        await service.ClaimAsync(queue.Id, 1);
        await service.CompleteAsync(done, QueueOutcome.Success("ok"));
        running.SessionId = "session-1";
        await service.CompleteAsync(running, QueueOutcome.Success("ok"));

        // One of the three is still in flight when the operator empties the queue.
        var inFlight = Assert.Single(await service.ClaimAsync(queue.Id, 1));

        Assert.Equal(3, await service.PurgeAsync(queue.Id, everything: true));
        Assert.Empty(await service.GetItemsAsync(queue.Id));

        // The agent that was working on it finishes after the row has gone.
        await service.CompleteAsync(inFlight, QueueOutcome.Success("finished anyway"));
        Assert.Empty(await service.GetItemsAsync(queue.Id));
    }

    [Fact]
    public async Task A_deleted_item_is_not_resurrected_by_the_agent_finishing()
    {
        var (service, _, queue, _) = Build();

        await service.EnqueueAsync(Item(queue, "mr-42"));
        var claimed = Assert.Single(await service.ClaimAsync(queue.Id, 1));

        await service.DeleteItemAsync(claimed.Id);
        await service.CompleteAsync(claimed, QueueOutcome.Success("finished anyway"));

        Assert.Empty(await service.GetItemsAsync(queue.Id));
    }

    [Fact]
    public void A_prompt_template_reads_the_item_and_its_payload()
    {
        var queue = new WorkQueue
        {
            Name = "Merge requests",
            PromptTemplate = "Review {{item.key}} ({{item.title}}) on {{repo}} — {{payload.author}}",
        };

        var item = new QueueItem
        {
            Key = "https://gitlab.com/acme/web/-/merge_requests/42",
            Title = "Add the queue page",
            Payload = new Dictionary<string, string> { ["repo"] = "acme/web", ["author"] = "sam" },
        };

        Assert.Equal(
            "Review https://gitlab.com/acme/web/-/merge_requests/42 (Add the queue page) on acme/web — sam",
            QueueContext.RenderPrompt(queue, item));
    }

    [Fact]
    public void An_item_overrides_a_queue_input_of_the_same_name()
    {
        var queue = new WorkQueue
        {
            Inputs = new Dictionary<string, string> { ["branch"] = "main" },
            PromptTemplate = "on {{branch}}",
        };

        var item = new QueueItem { Payload = new Dictionary<string, string> { ["branch"] = "release" } };

        Assert.Equal("on release", QueueContext.RenderPrompt(queue, item));
    }

    [Fact]
    public void An_empty_template_hands_the_agent_the_item_as_it_stands()
    {
        var queue = new WorkQueue { PromptTemplate = string.Empty };
        var item = new QueueItem { Key = "mr-42", Title = "Add the queue page", Body = "It needs a review." };

        var prompt = QueueContext.RenderPrompt(queue, item);

        Assert.Contains("Add the queue page", prompt);
        Assert.Contains("mr-42", prompt);
        Assert.Contains("It needs a review.", prompt);
    }

    [Fact]
    public async Task The_dispatcher_runs_the_queue_agent_once_per_item()
    {
        var (service, _, queue, sessions) = Build(q => q.MaxConcurrent = 4);

        await service.EnqueueAsync(Item(queue, "one"));
        await service.EnqueueAsync(Item(queue, "two"));

        await Dispatcher(service, sessions).PokeAsync();
        await WaitForSettledAsync(service, queue.Id);

        var items = await service.GetItemsAsync(queue.Id);
        Assert.All(items, i => Assert.Equal(QueueItemStatus.Succeeded, i.Status));
        Assert.Equal(2, sessions.Started.Count);
        Assert.All(sessions.Started, r => Assert.Equal(SessionTrigger.Queue, r.Trigger));
        // Every session is traceable back to the item that caused it.
        Assert.Equal(
            items.Select(i => i.Id).OrderBy(i => i),
            sessions.Started.Select(r => r.QueueItemId!).OrderBy(i => i));
    }

    [Fact]
    public async Task A_session_started_for_an_item_is_closed_and_read_only_once_the_item_is_done()
    {
        var (service, _, queue, sessions) = Build();

        await service.EnqueueAsync(Item(queue, "mr-42"));

        await Dispatcher(service, sessions).PokeAsync();
        await WaitForSettledAsync(service, queue.Id, expected: 1);

        var item = Assert.Single(await service.GetItemsAsync(queue.Id));
        var session = sessions.SessionFor(item.Id);

        Assert.NotNull(session);
        Assert.True(session!.IsClosed);
        Assert.False(session.AcceptsInput);
        Assert.Equal(SessionStatus.Completed, session.Status);
        Assert.Contains(queue.Name, session.ClosedReason);
    }

    [Fact]
    public async Task A_session_whose_item_failed_is_closed_still_saying_it_failed()
    {
        var (service, _, queue, sessions) = Build(q => q.MaxAttempts = 1);
        sessions.Fail = true;

        await service.EnqueueAsync(Item(queue, "mr-42"));

        await Dispatcher(service, sessions).PokeAsync();
        await WaitForSettledAsync(service, queue.Id, expected: 1);

        var item = Assert.Single(await service.GetItemsAsync(queue.Id));
        var session = sessions.SessionFor(item.Id);

        Assert.NotNull(session);
        Assert.True(session!.IsClosed);
        // Closing settles a run that finished; it does not rewrite one that fell over.
        Assert.Equal(SessionStatus.Failed, session.Status);
        Assert.Equal(QueueItemStatus.Failed, item.Status);
    }

    [Fact]
    public async Task The_dispatcher_starts_no_more_than_the_queue_allows_at_once()
    {
        var (service, _, queue, sessions) = Build(q => q.MaxConcurrent = 2);
        sessions.Gate = new TaskCompletionSource();

        for (var i = 0; i < 5; i++)
            await service.EnqueueAsync(Item(queue, $"item-{i}"));

        await Dispatcher(service, sessions).PokeAsync();

        // Nothing has been allowed to finish, so the slots are still occupied.
        var counts = await service.GetCountsAsync(queue.Id);
        Assert.Equal(2, counts.Running);
        Assert.Equal(3, counts.Pending);

        sessions.Gate.SetResult();
        await WaitForSettledAsync(service, queue.Id, expected: 2);
    }

    [Fact]
    public async Task A_failing_agent_fails_the_item()
    {
        var (service, _, queue, sessions) = Build();
        sessions.Fail = true;

        await service.EnqueueAsync(Item(queue, "mr-42"));

        await Dispatcher(service, sessions).PokeAsync();
        await WaitForSettledAsync(service, queue.Id);

        var stored = Assert.Single(await service.GetItemsAsync(queue.Id));
        Assert.Equal(QueueItemStatus.Failed, stored.Status);
        Assert.Equal("exploded", stored.LastError);
    }

    [Fact]
    public async Task A_queue_with_no_handler_collects_items_and_starts_nothing()
    {
        var (service, _, queue, sessions) = Build(q => q.TargetId = string.Empty);

        Assert.True((await service.EnqueueAsync(Item(queue, "mr-42"))).Accepted);
        Assert.True((await service.EnqueueAsync(Item(queue, "mr-43"))).Accepted);

        await Dispatcher(service, sessions).PokeAsync();

        Assert.Empty(sessions.Started);
        // The backlog is intact and waiting for somebody to choose what drains it.
        Assert.Equal(2, (await service.GetCountsAsync(queue.Id)).Pending);
    }

    [Fact]
    public async Task Work_collected_before_a_handler_existed_is_picked_up_once_one_is_set()
    {
        var (service, _, queue, sessions) = Build(q => q.TargetId = string.Empty);

        await service.EnqueueAsync(Item(queue, "mr-42"));
        await Dispatcher(service, sessions).PokeAsync();
        Assert.Empty(sessions.Started);

        queue.TargetId = "agent-1";

        await Dispatcher(service, sessions).PokeAsync();
        await WaitForSettledAsync(service, queue.Id, expected: 1);

        Assert.Single(sessions.Started);
        Assert.Equal(QueueItemStatus.Succeeded, Assert.Single(await service.GetItemsAsync(queue.Id)).Status);
    }

    [Fact]
    public async Task A_paused_queue_dispatches_nothing()
    {
        var (service, _, queue, sessions) = Build(q => q.Enabled = false);

        await service.EnqueueAsync(Item(queue, "mr-42"));
        await Dispatcher(service, sessions).PokeAsync();

        Assert.Empty(sessions.Started);
        // Items still arrive while paused; they simply wait.
        Assert.Equal(1, (await service.GetCountsAsync(queue.Id)).Pending);
    }

    [Fact]
    public async Task A_queue_can_be_named_instead_of_identified_when_enqueueing()
    {
        var (service, _, queue, _) = Build();

        var byName = await service.EnqueueAsync(Item(queue, "mr-1") with { QueueId = "Merge requests" });
        var byCasing = await service.EnqueueAsync(Item(queue, "mr-2") with { QueueId = "merge REQUESTS" });
        var bySlug = await service.EnqueueAsync(Item(queue, "mr-3") with { QueueId = "merge-requests" });

        Assert.True(byName.Accepted);
        Assert.True(byCasing.Accepted);
        Assert.True(bySlug.Accepted);
        Assert.All(
            new[] { byName, byCasing, bySlug },
            r => Assert.Equal(queue.Id, r.Item!.QueueId));
    }

    [Fact]
    public async Task A_partial_name_finds_the_queue_when_only_one_matches()
    {
        var (service, _, queue, _) = Build();

        Assert.Equal(queue.Id, (await service.ResolveQueueAsync("merge"))?.Id);
    }

    [Fact]
    public async Task An_ambiguous_partial_name_resolves_to_nothing_rather_than_the_wrong_queue()
    {
        var (service, _, _, _, queues) = BuildWithStore();
        await queues.UpsertAsync(new WorkQueue { Name = "Merge request reviews" });

        // Both queues contain "merge": filing the work in either would be a guess.
        Assert.Null(await service.ResolveQueueAsync("merge"));

        // The full name is still unambiguous even though it is a prefix of the other.
        Assert.Equal("Merge requests", (await service.ResolveQueueAsync("Merge requests"))?.Name);
    }

    [Fact]
    public async Task Enqueueing_onto_an_unknown_queue_says_what_the_queues_are()
    {
        var (service, _, _, _) = Build();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.EnqueueAsync(new EnqueueRequest { QueueId = "code review", Key = "x" }));

        Assert.Contains("Merge requests", error.Message);
    }

    private static EnqueueRequest Item(WorkQueue queue, string key, string title = "") => new()
    {
        QueueId = queue.Id,
        Key = key,
        Title = title.Length > 0 ? title : key,
    };

    private static QueueDispatcherHostedService Dispatcher(IQueueService service, RecordingSessionManager sessions) =>
        new(service,
            sessions,
            new StubWorkflowEngine(),
            Options.Create(new OrchestratorOptions()),
            NullLogger<QueueDispatcherHostedService>.Instance);

    /// <summary>
    /// The dispatcher hands each item to a background task, so a test has to wait for the work
    /// rather than for the call that started it.
    /// </summary>
    private static async Task WaitForSettledAsync(IQueueService service, string queueId, int expected = 0)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var counts = await service.GetCountsAsync(queueId);
            if (counts.Running == 0 && (expected == 0 || counts.Succeeded + counts.Failed >= expected))
                return;

            await Task.Delay(20);
        }

        Assert.Fail("The queue never settled.");
    }

    private static (QueueService Service, InMemoryStore<QueueItem> Items, WorkQueue Queue, RecordingSessionManager Sessions)
        Build(Action<WorkQueue>? configure = null)
    {
        var (service, items, queue, sessions, _) = BuildWithStore(configure);
        return (service, items, queue, sessions);
    }

    /// <summary>The same fixture, with the queue store handed back so a test can add a second queue.</summary>
    private static (QueueService Service, InMemoryStore<QueueItem> Items, WorkQueue Queue,
        RecordingSessionManager Sessions, InMemoryStore<WorkQueue> Queues)
        BuildWithStore(Action<WorkQueue>? configure = null)
    {
        var queues = new InMemoryStore<WorkQueue>();
        var items = new InMemoryStore<QueueItem>();
        var sessions = new RecordingSessionManager();

        var queue = new WorkQueue { Name = "Merge requests", Target = QueueTarget.Agent, TargetId = "agent-1" };
        configure?.Invoke(queue);
        queues.UpsertAsync(queue).GetAwaiter().GetResult();

        var service = new QueueService(
            queues, items, sessions, new StubWorkflowEngine(), NullLogger<QueueService>.Instance);

        return (service, items, queue, sessions, queues);
    }

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

    /// <summary>Records what it was asked to start, and can be held open to observe the slot cap.</summary>
    private sealed class RecordingSessionManager : ISessionManager
    {
        private readonly ConcurrentDictionary<string, ChatSession> _sessions = new();

        public List<StartSessionRequest> Started { get; } = [];
        public List<string> Cancelled { get; } = [];
        public List<string> Closed { get; } = [];
        public bool Fail { get; set; }

        public ChatSession? SessionFor(string queueItemId) =>
            _sessions.Values.FirstOrDefault(s => s.QueueItemId == queueItemId);

        /// <summary>Set to stop sessions finishing until the test lets them.</summary>
        public TaskCompletionSource? Gate { get; set; }

        public IReadOnlyList<ChatSession> Live => [];

        public event Action<ChatSession>? SessionUpdated;

        public async Task<ChatSession> StartAsync(StartSessionRequest request, CancellationToken ct = default)
        {
            lock (Started)
                Started.Add(request);

            if (Gate is not null)
                await Gate.Task;

            var session = new ChatSession
            {
                AgentId = request.AgentId,
                Title = request.Title ?? request.Prompt,
                Status = Fail ? SessionStatus.Failed : SessionStatus.AwaitingInput,
                LastError = Fail ? "exploded" : null,
                QueueItemId = request.QueueItemId,
                Messages = [new ChatMessage { Role = MessageRole.Agent, Content = request.Prompt }],
            };

            _sessions[session.Id] = session;
            SessionUpdated?.Invoke(session);
            return session;
        }

        public Task<ChatSession> RunToCompletionAsync(StartSessionRequest request, TimeSpan timeout, CancellationToken ct = default) =>
            StartAsync(request, ct);

        public Task<ChatSession?> CloseAsync(string sessionId, string? reason = null, CancellationToken ct = default)
        {
            lock (Closed)
                Closed.Add(sessionId);

            if (!_sessions.TryGetValue(sessionId, out var session))
                return Task.FromResult<ChatSession?>(null);

            if (session.Status is not (SessionStatus.Failed or SessionStatus.Cancelled))
                session.Status = SessionStatus.Completed;

            session.IsClosed = true;
            session.ClosedReason = reason;
            return Task.FromResult<ChatSession?>(session);
        }

        public Task CancelAsync(string sessionId)
        {
            lock (Cancelled)
                Cancelled.Add(sessionId);

            return Task.CompletedTask;
        }

        public Task<ChatSession?> SetStatusAsync(
            string sessionId,
            SessionStatus status,
            CancellationToken ct = default) => Task.FromResult<ChatSession?>(null);

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

        public Task<ChatSession?> GetAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult<ChatSession?>(null);

        public Task<IReadOnlyList<ChatSession>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ChatSession>>([]);

        public Task<bool> DeleteAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class StubWorkflowEngine : IWorkflowEngine
    {
        public IReadOnlyList<WorkflowRun> Active => [];

        public event Action<WorkflowRun>? RunUpdated;

        public Task<WorkflowRun> RunAsync(
            string workflowId,
            IReadOnlyDictionary<string, string> inputs,
            string triggeredBy,
            CancellationToken ct = default)
        {
            var run = new WorkflowRun { WorkflowId = workflowId, Status = RunStatus.Succeeded };
            RunUpdated?.Invoke(run);
            return Task.FromResult(run);
        }

        public Task<WorkflowRun> StartAsync(
            string workflowId,
            IReadOnlyDictionary<string, string> inputs,
            string triggeredBy,
            CancellationToken ct = default) =>
            RunAsync(workflowId, inputs, triggeredBy, ct);

        public Task CancelAsync(string runId) => Task.CompletedTask;

        public Task<IReadOnlyList<WorkflowRun>> GetRunsAsync(string? workflowId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowRun>>([]);

        public Task<WorkflowRun?> GetRunAsync(string runId, CancellationToken ct = default) =>
            Task.FromResult<WorkflowRun?>(null);
    }
}
