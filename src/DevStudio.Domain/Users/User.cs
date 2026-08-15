using DevStudio.Domain.Common;

namespace DevStudio.Domain.Users;

/// <summary>
/// Someone who can sign in. There are no roles: every account has the same full access, so the only
/// question this record answers is "is this person allowed in at all".
/// </summary>
public sealed class User : Entity
{
    /// <summary>What they sign in with. Compared case-insensitively and stored lower-cased.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Display name, shown in the top bar and on the users page.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>PBKDF2 hash in the self-describing format produced by the password hasher.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Turned off rather than deleted, so a sign-in is refused without losing the record.</summary>
    public bool Enabled { get; set; } = true;

    public DateTimeOffset? LastSignInAt { get; set; }
}
