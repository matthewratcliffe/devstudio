using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Projects;
using DevStudio.Domain.Providers;
using DevStudio.Infrastructure.Persistence;
using DevStudio.Infrastructure.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevStudio.Tests;

public class AccountServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devstudio-accounts-" + Guid.NewGuid().ToString("n"));
    private readonly OrchestratorOptions _options;
    private readonly JsonEntityStore<ProviderAccount> _accounts;
    private readonly JsonEntityStore<Project> _projects;
    private readonly AccountService _service;

    public AccountServiceTests()
    {
        _options = new OrchestratorOptions
        {
            DataPath = _root,
            HomePath = Path.Combine(_root, "home"),
        };

        var options = Options.Create(_options);
        _accounts = new JsonEntityStore<ProviderAccount>(options, NullLogger<JsonEntityStore<ProviderAccount>>.Instance);
        _projects = new JsonEntityStore<Project>(options, NullLogger<JsonEntityStore<Project>>.Instance);

        _service = new AccountService(
            _accounts,
            _projects,
            new StubRegistry(),
            options,
            NullLogger<AccountService>.Instance);
    }

    [Fact]
    public async Task A_project_account_beats_the_agent_pin_and_the_default()
    {
        var work = await _service.CreateAsync("Work", AiProvider.Claude);
        var personal = await _service.CreateAsync("Personal", AiProvider.Claude);

        var project = await _projects.UpsertAsync(new Project { Name = "Client", ClaudeAccountId = personal.Id });
        var agent = new Agent { Provider = AiProvider.Claude, AccountId = work.Id };

        var resolved = await _service.ResolveAsync(agent, project.Id);

        Assert.Equal(personal.Id, resolved.AccountId);
        Assert.Equal("Personal", resolved.Name);
    }

    [Fact]
    public async Task The_agent_pin_applies_when_there_is_no_project()
    {
        await _service.CreateAsync("Personal", AiProvider.Claude);
        var work = await _service.CreateAsync("Work", AiProvider.Claude);

        var resolved = await _service.ResolveAsync(new Agent { Provider = AiProvider.Claude, AccountId = work.Id }, null);

        Assert.Equal(work.Id, resolved.AccountId);
    }

    [Fact]
    public async Task Falls_back_to_the_default_account_for_the_provider()
    {
        var first = await _service.CreateAsync("Personal", AiProvider.Claude);
        await _service.CreateAsync("Work", AiProvider.Claude);

        var resolved = await _service.ResolveAsync(new Agent { Provider = AiProvider.Claude }, null);

        // The first account created for a provider becomes its default.
        Assert.Equal(first.Id, resolved.AccountId);
        Assert.True(first.IsDefault);
    }

    [Fact]
    public async Task The_next_account_is_available_as_a_backup()
    {
        var primary = await _service.CreateAsync("Personal", AiProvider.Claude);
        var backup = await _service.CreateAsync("Work", AiProvider.Claude);

        var resolved = await _service.ResolveAsync(new Agent { Provider = AiProvider.Claude }, null);

        Assert.Equal(primary.Id, resolved.AccountId);
        Assert.Equal(backup.Id, resolved.Fallback!.AccountId);
        Assert.Equal(backup.HomePath, resolved.Fallback.HomePath);
    }

    [Fact]
    public async Task A_codex_agent_never_resolves_to_a_claude_account()
    {
        await _service.CreateAsync("Personal", AiProvider.Claude);

        var resolved = await _service.ResolveAsync(new Agent { Provider = AiProvider.Codex }, null);

        Assert.Null(resolved.AccountId);
        Assert.Equal(_options.HomePath, resolved.HomePath);
    }

    [Fact]
    public async Task Each_account_gets_its_own_credential_directory()
    {
        var work = await _service.CreateAsync("Work", AiProvider.Claude);
        var personal = await _service.CreateAsync("Personal", AiProvider.Claude);

        Assert.NotEqual(work.HomePath, personal.HomePath);
        Assert.True(Directory.Exists(Path.Combine(work.HomePath, ".claude")));
        Assert.True(Directory.Exists(Path.Combine(personal.HomePath, ".codex")));
    }

    [Fact]
    public async Task Two_accounts_cannot_share_a_name_within_a_provider()
    {
        await _service.CreateAsync("Work", AiProvider.Claude);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateAsync("work", AiProvider.Claude));

        // The same name under the other CLI is fine — they are separate logins.
        var codex = await _service.CreateAsync("Work", AiProvider.Codex);
        Assert.NotNull(codex);
    }

    [Fact]
    public async Task Deleting_the_default_promotes_another_account()
    {
        var first = await _service.CreateAsync("Personal", AiProvider.Claude);
        var second = await _service.CreateAsync("Work", AiProvider.Claude);

        await _service.DeleteAsync(first.Id, deleteCredentials: true);

        var remaining = await _accounts.GetAsync(second.Id);
        Assert.True(remaining!.IsDefault);
    }

    [Fact]
    public async Task Deleting_an_account_never_removes_the_shared_container_home()
    {
        Directory.CreateDirectory(_options.HomePath);
        var shared = await _accounts.UpsertAsync(new ProviderAccount
        {
            Name = "Default",
            Provider = AiProvider.Claude,
            HomePath = _options.HomePath,
        });

        await _service.DeleteAsync(shared.Id, deleteCredentials: true);

        Assert.True(Directory.Exists(_options.HomePath));
    }

    private sealed class StubRegistry : IProviderCliRegistry
    {
        public IReadOnlyList<IProviderCli> All => [];

        public IProviderCli Get(AiProvider provider) => throw new NotSupportedException();

        public Task<IProviderCli> ResolveAsync(AiProvider provider, string? cliProviderId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<IProviderCli>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IProviderCli>>([]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }
}
