using AiShop.Domain.Common;

namespace AiShop.Domain.Providers;

/// <summary>
/// One logged-in identity for a CLI — a personal Claude and a work Claude are two accounts.
/// Each owns a home directory holding its own <c>.claude</c> / <c>.codex</c> credentials; switching
/// account is switching which HOME the CLI process runs with.
/// </summary>
public sealed class ProviderAccount : Entity
{
    public string Name { get; set; } = "New account";
    public string Description { get; set; } = string.Empty;
    public AiProvider Provider { get; set; } = AiProvider.Claude;

    /// <summary>Which user-defined CLI this login belongs to, when the provider is Custom.</summary>
    public string? CliProviderId { get; set; }

    /// <summary>
    /// Absolute home directory for this account. The seeded default points at the container home so
    /// an existing login keeps working; new accounts get their own folder on the data volume.
    /// </summary>
    public string HomePath { get; set; } = string.Empty;

    /// <summary>Used when nothing more specific applies to a session.</summary>
    public bool IsDefault { get; set; }

    /// <summary>Last known login state, refreshed whenever the Logins page is opened.</summary>
    public ProviderAuthState LastKnownState { get; set; } = ProviderAuthState.Unknown;
    public DateTimeOffset? LastCheckedAt { get; set; }

    /// <summary>Free-text label for whose account this is, e.g. the email you logged in with.</summary>
    public string? Account { get; set; }
}
