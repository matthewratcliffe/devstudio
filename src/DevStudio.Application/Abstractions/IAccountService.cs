using DevStudio.Domain.Agents;
using DevStudio.Domain.Providers;

namespace DevStudio.Application.Abstractions;

/// <summary>The account a session will run under, and the home directory that makes it so.</summary>
public sealed record ResolvedAccount(string? AccountId, string Name, string HomePath);

/// <summary>
/// Picks which logged-in identity a CLI runs as. Precedence: the project's choice for that provider,
/// then the agent's own pin, then the provider's default account, then the container home.
/// </summary>
public interface IAccountService
{
    Task<ResolvedAccount> ResolveAsync(Agent agent, string? projectId, CancellationToken ct = default);

    /// <summary>Home directory for a specific account, creating it if this is its first use.</summary>
    Task<string> GetHomePathAsync(string accountId, CancellationToken ct = default);

    /// <summary>Creates an account with its own credential directory.</summary>
    Task<ProviderAccount> CreateAsync(string name, AiProvider provider, CancellationToken ct = default);

    /// <summary>Creates an account for a user-defined CLI.</summary>
    Task<ProviderAccount> CreateAsync(string name, AiProvider provider, string? cliProviderId, CancellationToken ct = default);

    /// <summary>Deletes the account, and optionally the credentials with it.</summary>
    Task<bool> DeleteAsync(string accountId, bool deleteCredentials, CancellationToken ct = default);

    /// <summary>Refreshes the stored login state for every account by probing its CLI.</summary>
    Task<IReadOnlyList<ProviderAccount>> RefreshAuthStateAsync(CancellationToken ct = default);
}
