using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Projects;
using DevStudio.Domain.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevStudio.Infrastructure.Providers;

/// <summary>
/// Resolves which login a session runs under. Accounts are just separate home directories, so
/// switching between a personal and a work Claude is switching HOME for the child process — no
/// credential juggling and nothing shared between them.
/// </summary>
public sealed class AccountService : IAccountService
{
    private readonly IEntityStore<ProviderAccount> _accounts;
    private readonly IEntityStore<Project> _projects;
    private readonly IProviderCliRegistry _clis;
    private readonly OrchestratorOptions _options;
    private readonly ILogger<AccountService> _logger;

    public AccountService(
        IEntityStore<ProviderAccount> accounts,
        IEntityStore<Project> projects,
        IProviderCliRegistry clis,
        IOptions<OrchestratorOptions> options,
        ILogger<AccountService> logger)
    {
        _accounts = accounts;
        _projects = projects;
        _clis = clis;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ResolvedAccount> ResolveAsync(Agent agent, string? projectId, CancellationToken ct = default)
    {
        var all = await _accounts.GetAllAsync(ct);

        // 1. The project's choice for this provider.
        projectId ??= agent.ProjectId;
        if (projectId is not null && await _projects.GetAsync(projectId, ct) is { } project)
        {
            var chosen = agent.Provider switch
            {
                AiProvider.Claude => project.ClaudeAccountId,
                AiProvider.Codex => project.CodexAccountId,
                _ => agent.CliProviderId is not null && project.CliAccountIds.TryGetValue(agent.CliProviderId, out var custom)
                    ? custom
                    : null,
            };

            var projectFallback = agent.Provider switch
            {
                AiProvider.Claude => project.ClaudeFallbackAccountId,
                AiProvider.Codex => project.CodexFallbackAccountId,
                _ => null,
            };

            if (chosen is not null && all.FirstOrDefault(a => a.Id == chosen) is { } projectAccount)
                return await WithFallbackAsync(await MaterialiseAsync(projectAccount), all, agent, projectAccount, projectFallback, ct);
        }

        // 2. The agent's own pin, for work outside a project.
        if (agent.AccountId is not null && all.FirstOrDefault(a => a.Id == agent.AccountId) is { } agentAccount)
            return await WithFallbackAsync(await MaterialiseAsync(agentAccount), all, agent, agentAccount, agent.FallbackAccountId, ct);

        // 3. The default account for this provider — for a custom CLI, one of its own accounts.
        bool Matches(ProviderAccount account) =>
            account.Provider == agent.Provider &&
            (agent.Provider != AiProvider.Custom || account.CliProviderId == agent.CliProviderId);

        var fallback = all.FirstOrDefault(a => Matches(a) && a.IsDefault) ?? all.FirstOrDefault(Matches);

        return fallback is not null
            ? await WithFallbackAsync(await MaterialiseAsync(fallback), all, agent, fallback, agent.FallbackAccountId, ct)
            : new ResolvedAccount(null, "container default", _options.HomePath);
    }

    private async Task<ResolvedAccount> WithFallbackAsync(
        ResolvedAccount primary,
        IReadOnlyList<ProviderAccount> all,
        Agent agent,
        ProviderAccount primaryAccount,
        string? requestedFallbackId,
        CancellationToken ct)
    {
        // The default account is the primary; the next account for the same CLI is the backup.
        // Explicit project/agent pins still get the same safety net, but never fall back to the
        // account they already selected.
        bool Matches(ProviderAccount account) =>
            account.Id != primaryAccount.Id &&
            account.Provider == primaryAccount.Provider &&
            (agent.Provider != AiProvider.Custom || account.CliProviderId == agent.CliProviderId);

        var backup = !string.IsNullOrWhiteSpace(requestedFallbackId)
            ? all.FirstOrDefault(account => account.Id == requestedFallbackId && Matches(account))
            : null;

        // Preserve the old automatic behavior when no explicit fallback was selected.
        backup ??= all.FirstOrDefault(Matches);

        return backup is null
            ? primary
            : primary with { Fallback = await MaterialiseAsync(backup) };
    }

    public async Task<string> GetHomePathAsync(string accountId, CancellationToken ct = default)
    {
        var account = await _accounts.GetAsync(accountId, ct)
                      ?? throw new InvalidOperationException("That account no longer exists.");

        return (await MaterialiseAsync(account)).HomePath;
    }

    public Task<ProviderAccount> CreateAsync(string name, AiProvider provider, CancellationToken ct = default) =>
        CreateAsync(name, provider, null, ct);

    public async Task<ProviderAccount> CreateAsync(string name, AiProvider provider, string? cliProviderId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("An account needs a name.", nameof(name));

        var all = await _accounts.GetAllAsync(ct);
        var slug = TemplateRenderer.Slugify(name);

        bool SameProvider(ProviderAccount a) =>
            a.Provider == provider && (provider != AiProvider.Custom || a.CliProviderId == cliProviderId);

        if (all.Any(a => SameProvider(a) && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"There is already an account called '{name}' for that CLI.");

        var folder = provider == AiProvider.Custom
            ? $"cli-{cliProviderId}-{slug}"
            : $"{provider.ToString().ToLowerInvariant()}-{slug}";

        var account = new ProviderAccount
        {
            Name = name.Trim(),
            Provider = provider,
            CliProviderId = cliProviderId,
            HomePath = Path.Combine(_options.DataPath, "accounts", folder),
            // The first account for a CLI becomes its default.
            IsDefault = !all.Any(SameProvider),
        };

        await MaterialiseAsync(account);
        return await _accounts.UpsertAsync(account, ct);
    }

    public async Task<bool> DeleteAsync(string accountId, bool deleteCredentials, CancellationToken ct = default)
    {
        var account = await _accounts.GetAsync(accountId, ct);
        if (account is null)
            return false;

        // Never delete the shared container home, whatever the caller asks for.
        var ownsItsDirectory = !PathsMatch(account.HomePath, _options.HomePath);

        if (deleteCredentials && ownsItsDirectory && Directory.Exists(account.HomePath))
        {
            try
            {
                Directory.Delete(account.HomePath, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete the credentials for account {Account}", account.Name);
            }
        }

        var removed = await _accounts.DeleteAsync(accountId, ct);

        // Keep exactly one default per provider.
        if (removed && account.IsDefault)
        {
            var remaining = (await _accounts.GetAllAsync(ct))
                .Where(a => a.Provider == account.Provider && a.CliProviderId == account.CliProviderId)
                .ToList();
            if (remaining.FirstOrDefault() is { } promoted)
            {
                promoted.IsDefault = true;
                await _accounts.UpsertAsync(promoted, ct);
            }
        }

        return removed;
    }

    public async Task<IReadOnlyList<ProviderAccount>> RefreshAuthStateAsync(CancellationToken ct = default)
    {
        var accounts = await _accounts.GetAllAsync(ct);

        foreach (var account in accounts)
        {
            var home = (await MaterialiseAsync(account)).HomePath;

            try
            {
                var cli = await _clis.ResolveAsync(account.Provider, account.CliProviderId, ct);
                var status = await cli.GetAuthStatusAsync(home, ct);

                account.LastKnownState = status.State;
                account.LastCheckedAt = status.CheckedAt;
            }
            catch (Exception ex)
            {
                // A definition that has been deleted or disabled must not break the whole page.
                _logger.LogWarning(ex, "Could not probe account {Account}", account.Name);
                account.LastKnownState = ProviderAuthState.Unknown;
                account.LastCheckedAt = DateTimeOffset.UtcNow;
            }

            await _accounts.UpsertAsync(account, ct);
        }

        return accounts;
    }

    /// <summary>Makes sure the account's home exists before a CLI is pointed at it.</summary>
    private Task<ResolvedAccount> MaterialiseAsync(ProviderAccount account)
    {
        var home = string.IsNullOrWhiteSpace(account.HomePath) ? _options.HomePath : account.HomePath;

        try
        {
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(Path.Combine(home, ".claude"));
            Directory.CreateDirectory(Path.Combine(home, ".codex"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not prepare the home directory for account {Account}", account.Name);
        }

        return Task.FromResult(new ResolvedAccount(account.Id, account.Name, home));
    }

    private static bool PathsMatch(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
