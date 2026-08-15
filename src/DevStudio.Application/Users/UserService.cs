using DevStudio.Application.Abstractions;
using DevStudio.Domain.Users;
using Microsoft.Extensions.Logging;

namespace DevStudio.Application.Users;

/// <inheritdoc />
public sealed class UserService : IUserService
{
    /// <summary>Username and password of the account created when there are no accounts at all.</summary>
    public const string SeedUsername = "admin";
    public const string SeedPassword = "admin";

    private readonly IEntityStore<User> _users;
    private readonly ILogger<UserService> _logger;

    public UserService(IEntityStore<User> users, ILogger<UserService> logger)
    {
        _users = users;
        _logger = logger;
    }

    public async Task<IReadOnlyList<User>> GetAllAsync(CancellationToken ct = default)
    {
        var all = await _users.GetAllAsync(ct);
        return [.. all.OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase)];
    }

    public Task<User?> GetAsync(string id, CancellationToken ct = default) => _users.GetAsync(id, ct);

    public async Task<User?> AuthenticateAsync(string username, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            return null;

        var user = await FindByUsernameAsync(Normalise(username), ct);

        // Verified even when there is no such account, so a wrong username and a wrong password take
        // the same time to answer and cannot be told apart by timing them.
        var correct = PasswordHasher.Verify(password, user?.PasswordHash ?? string.Empty);

        if (user is null || !correct || !user.Enabled)
            return null;

        user.LastSignInAt = DateTimeOffset.UtcNow;
        await _users.UpsertAsync(user, ct);

        return user;
    }

    public async Task<User> CreateAsync(string username, string name, string password, CancellationToken ct = default)
    {
        var normalised = Normalise(username);
        Validate(normalised, name, password);

        if (await FindByUsernameAsync(normalised, ct) is not null)
            throw new InvalidOperationException($"'{normalised}' is already taken.");

        return await _users.UpsertAsync(
            new User
            {
                Username = normalised,
                Name = name.Trim(),
                PasswordHash = PasswordHasher.Hash(password),
            },
            ct);
    }

    public async Task<User> UpdateAsync(string id, string username, string name, bool enabled, CancellationToken ct = default)
    {
        var user = await Require(id, ct);
        var normalised = Normalise(username);
        Validate(normalised, name, password: null);

        var clash = await FindByUsernameAsync(normalised, ct);
        if (clash is not null && clash.Id != user.Id)
            throw new InvalidOperationException($"'{normalised}' is already taken.");

        // Nobody can sign in once the last working account is switched off, and the only way back
        // would be editing JSON on the volume by hand.
        if (!enabled && user.Enabled && await CountEnabledAsync(ct) <= 1)
            throw new InvalidOperationException("This is the only account that can sign in — leave it enabled.");

        user.Username = normalised;
        user.Name = name.Trim();
        user.Enabled = enabled;

        return await _users.UpsertAsync(user, ct);
    }

    public async Task SetPasswordAsync(string id, string password, CancellationToken ct = default)
    {
        var user = await Require(id, ct);
        RequirePasswordLength(password);

        user.PasswordHash = PasswordHasher.Hash(password);
        await _users.UpsertAsync(user, ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        var user = await Require(id, ct);

        if (user.Enabled && await CountEnabledAsync(ct) <= 1)
            throw new InvalidOperationException("This is the only account that can sign in — make another one first.");

        await _users.DeleteAsync(user.Id, ct);
    }

    public async Task<bool> EnsureSeedAccountAsync(CancellationToken ct = default)
    {
        if ((await _users.GetAllAsync(ct)).Count > 0)
            return false;

        await _users.UpsertAsync(
            new User
            {
                Username = SeedUsername,
                Name = "Administrator",
                PasswordHash = PasswordHasher.Hash(SeedPassword),
            },
            ct);

        _logger.LogWarning(
            "No accounts existed, so '{Username}' was created with the password '{Password}'. Change it.",
            SeedUsername,
            SeedPassword);

        return true;
    }

    public async Task<bool> UsesSeedPasswordAsync(CancellationToken ct = default)
    {
        var all = await _users.GetAllAsync(ct);
        return all.Any(u => u.Enabled && PasswordHasher.Verify(SeedPassword, u.PasswordHash));
    }

    private async Task<User> Require(string id, CancellationToken ct) =>
        await _users.GetAsync(id, ct) ?? throw new InvalidOperationException("That account no longer exists.");

    private async Task<User?> FindByUsernameAsync(string normalised, CancellationToken ct)
    {
        var all = await _users.GetAllAsync(ct);
        return all.FirstOrDefault(u => string.Equals(u.Username, normalised, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<int> CountEnabledAsync(CancellationToken ct) =>
        (await _users.GetAllAsync(ct)).Count(u => u.Enabled);

    /// <summary>Lower-cased and trimmed, so a username is one value however it was typed.</summary>
    private static string Normalise(string username) => (username ?? string.Empty).Trim().ToLowerInvariant();

    private static void Validate(string normalisedUsername, string name, string? password)
    {
        if (normalisedUsername.Length < 2 || normalisedUsername.Any(char.IsWhiteSpace))
            throw new InvalidOperationException("A username needs at least two characters and no spaces.");

        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("A display name is required.");

        if (password is not null)
            RequirePasswordLength(password);
    }

    private static void RequirePasswordLength(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < PasswordHasher.MinimumLength)
            throw new InvalidOperationException($"The password must be at least {PasswordHasher.MinimumLength} characters.");
    }
}
