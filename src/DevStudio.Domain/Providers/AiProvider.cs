namespace DevStudio.Domain.Providers;

/// <summary>The CLI an agent talks to. There is no API integration — every provider is a local process.</summary>
public enum AiProvider
{
    Claude = 0,
    Codex = 1,
    Opencoder = 3,

    /// <summary>
    /// A user-defined CLI. The agent, session or account also carries the id of the
    /// <see cref="CliProvider"/> that describes how to drive it.
    /// </summary>
    Custom = 2,
}

public enum ProviderAuthState
{
    Unknown = 0,
    LoggedOut = 1,
    LoggedIn = 2,
}

/// <summary>Result of probing a CLI for whether it currently holds valid credentials.</summary>
public sealed record ProviderAuthStatus(
    AiProvider Provider,
    ProviderAuthState State,
    string? Account,
    string? Detail,
    DateTimeOffset CheckedAt)
{
    public static ProviderAuthStatus Unknown(AiProvider provider, string? detail = null) =>
        new(provider, ProviderAuthState.Unknown, null, detail, DateTimeOffset.UtcNow);
}
