using DevStudio.Domain.Users;

namespace DevStudio.Application.Users;

/// <summary>
/// Accounts that can sign in. Deliberately flat: there are no roles and no permissions, so every
/// account this returns has full access to everything the app can do.
/// </summary>
public interface IUserService
{
    Task<IReadOnlyList<User>> GetAllAsync(CancellationToken ct = default);

    Task<User?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// The account for these credentials, or null when the username is unknown, the password is
    /// wrong, or the account is disabled. Stamps the sign-in time on success.
    /// </summary>
    Task<User?> AuthenticateAsync(string username, string password, CancellationToken ct = default);

    Task<User> CreateAsync(string username, string name, string password, CancellationToken ct = default);

    /// <summary>Renames an account and changes the name it signs in with. Password is untouched.</summary>
    Task<User> UpdateAsync(string id, string username, string name, bool enabled, CancellationToken ct = default);

    Task SetPasswordAsync(string id, string password, CancellationToken ct = default);

    Task DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Creates the built-in <c>admin</c> account when the volume has no accounts at all, and returns
    /// true when it did. Called once on start so a fresh install is reachable without editing files.
    /// </summary>
    Task<bool> EnsureSeedAccountAsync(CancellationToken ct = default);

    /// <summary>
    /// True while an account is still on the password it was seeded with. Drives the banner nagging
    /// the operator to change it, which is the only thing standing between a LAN and full access.
    /// </summary>
    Task<bool> UsesSeedPasswordAsync(CancellationToken ct = default);
}
