using AiShop.Application.Common;
using AiShop.Domain.Agents;
using AiShop.Domain.Providers;
using AiShop.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiShop.Tests;

public class JsonEntityStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aishop-tests-" + Guid.NewGuid().ToString("n"));

    private JsonEntityStore<Agent> CreateStore() =>
        new(Options.Create(new OrchestratorOptions { DataPath = _root }), NullLogger<JsonEntityStore<Agent>>.Instance);

    [Fact]
    public async Task Round_trips_an_entity()
    {
        var store = CreateStore();
        var agent = new Agent { Name = "Builder", Provider = AiProvider.Codex, PermissionMode = PermissionMode.Plan };

        await store.UpsertAsync(agent);
        var loaded = await store.GetAsync(agent.Id);

        Assert.NotNull(loaded);
        Assert.Equal("Builder", loaded.Name);
        Assert.Equal(AiProvider.Codex, loaded.Provider);
        Assert.Equal(PermissionMode.Plan, loaded.PermissionMode);
    }

    [Fact]
    public async Task State_survives_a_new_store_over_the_same_directory()
    {
        var agent = new Agent { Name = "Persisted" };
        await CreateStore().UpsertAsync(agent);

        // A fresh instance is what a container restart looks like.
        var reloaded = await CreateStore().GetAsync(agent.Id);

        Assert.Equal("Persisted", reloaded?.Name);
    }

    [Fact]
    public async Task Delete_removes_the_entity_and_its_file()
    {
        var store = CreateStore();
        var agent = await store.UpsertAsync(new Agent { Name = "Temporary" });

        Assert.True(await store.DeleteAsync(agent.Id));
        Assert.Null(await store.GetAsync(agent.Id));
        Assert.False(await store.DeleteAsync(agent.Id));
    }

    [Fact]
    public async Task Change_notifications_fire_on_write()
    {
        var store = CreateStore();
        Agent? seen = null;
        store.Changed += a => seen = a;

        await store.UpsertAsync(new Agent { Name = "Watched" });

        Assert.Equal("Watched", seen?.Name);
    }

    [Fact]
    public async Task A_corrupt_file_is_skipped_rather_than_failing_the_load()
    {
        var store = CreateStore();
        await store.UpsertAsync(new Agent { Name = "Good" });
        await File.WriteAllTextAsync(Path.Combine(_root, "agents", "broken.json"), "{ not json");

        var all = await CreateStore().GetAllAsync();

        Assert.Single(all);
        Assert.Equal("Good", all[0].Name);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }
}
