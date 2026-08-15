using System.Collections.Concurrent;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Users;
using DevStudio.Domain.Common;
using DevStudio.Domain.Users;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevStudio.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void Accepts_the_password_it_hashed()
    {
        var hash = PasswordHasher.Hash("correct horse");

        Assert.True(PasswordHasher.Verify("correct horse", hash));
    }

    [Fact]
    public void Rejects_a_different_password()
    {
        var hash = PasswordHasher.Hash("correct horse");

        Assert.False(PasswordHasher.Verify("Correct horse", hash));
        Assert.False(PasswordHasher.Verify(string.Empty, hash));
    }

    [Fact]
    public void Salts_every_hash_separately()
    {
        Assert.NotEqual(PasswordHasher.Hash("same"), PasswordHasher.Hash("same"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-hash")]
    [InlineData("v1.1000.notbase64.alsonot")]
    [InlineData("v2.1000.AAAA.AAAA")]
    [InlineData("v1.0.AAAA.AAAA")]
    public void Refuses_a_malformed_hash_without_throwing(string hash) =>
        Assert.False(PasswordHasher.Verify("anything", hash));
}

public class UserServiceTests
{
    [Fact]
    public async Task Seeds_an_admin_account_when_there_are_none()
    {
        var (service, store) = Build();

        Assert.True(await service.EnsureSeedAccountAsync());

        var seeded = Assert.Single(await store.GetAllAsync());
        Assert.Equal("admin", seeded.Username);
        Assert.True(PasswordHasher.Verify("admin", seeded.PasswordHash));
    }

    [Fact]
    public async Task Does_not_seed_a_second_time()
    {
        var (service, store) = Build();
        await service.EnsureSeedAccountAsync();
        await service.SetPasswordAsync((await store.GetAllAsync())[0].Id, "something else");

        Assert.False(await service.EnsureSeedAccountAsync());

        var user = Assert.Single(await store.GetAllAsync());
        Assert.True(PasswordHasher.Verify("something else", user.PasswordHash));
    }

    [Fact]
    public async Task Reports_while_the_seed_password_is_still_in_use()
    {
        var (service, store) = Build();
        await service.EnsureSeedAccountAsync();

        Assert.True(await service.UsesSeedPasswordAsync());

        await service.SetPasswordAsync((await store.GetAllAsync())[0].Id, "not the default");

        Assert.False(await service.UsesSeedPasswordAsync());
    }

    [Fact]
    public async Task Authenticates_regardless_of_username_casing()
    {
        var (service, _) = Build();
        await service.CreateAsync("Matt", "Matt R", "hunter2");

        Assert.NotNull(await service.AuthenticateAsync("MATT", "hunter2"));
        Assert.NotNull(await service.AuthenticateAsync("  matt ", "hunter2"));
    }

    [Fact]
    public async Task Refuses_a_wrong_password_and_an_unknown_username()
    {
        var (service, _) = Build();
        await service.CreateAsync("matt", "Matt R", "hunter2");

        Assert.Null(await service.AuthenticateAsync("matt", "hunter3"));
        Assert.Null(await service.AuthenticateAsync("nobody", "hunter2"));
    }

    [Fact]
    public async Task Refuses_a_disabled_account()
    {
        var (service, _) = Build();
        var user = await service.CreateAsync("matt", "Matt R", "hunter2");
        await service.CreateAsync("other", "Someone Else", "hunter2");
        await service.UpdateAsync(user.Id, "matt", "Matt R", enabled: false);

        Assert.Null(await service.AuthenticateAsync("matt", "hunter2"));
    }

    [Fact]
    public async Task Stamps_the_sign_in_time()
    {
        var (service, _) = Build();
        await service.CreateAsync("matt", "Matt R", "hunter2");

        var signedIn = await service.AuthenticateAsync("matt", "hunter2");

        Assert.NotNull(signedIn!.LastSignInAt);
    }

    [Fact]
    public async Task Refuses_a_username_already_taken()
    {
        var (service, _) = Build();
        await service.CreateAsync("matt", "Matt R", "hunter2");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateAsync("MATT", "Someone Else", "hunter2"));
    }

    [Theory]
    [InlineData("m", "Name", "hunter2")]
    [InlineData("has space", "Name", "hunter2")]
    [InlineData("matt", "", "hunter2")]
    [InlineData("matt", "Name", "abc")]
    public async Task Refuses_invalid_details(string username, string name, string password)
    {
        var (service, _) = Build();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateAsync(username, name, password));
    }

    [Fact]
    public async Task Will_not_delete_or_disable_the_last_account_that_can_sign_in()
    {
        var (service, _) = Build();
        var only = await service.CreateAsync("matt", "Matt R", "hunter2");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync(only.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateAsync(only.Id, "matt", "Matt R", enabled: false));
    }

    [Fact]
    public async Task Deletes_an_account_once_another_one_exists()
    {
        var (service, store) = Build();
        var first = await service.CreateAsync("matt", "Matt R", "hunter2");
        await service.CreateAsync("other", "Someone Else", "hunter2");

        await service.DeleteAsync(first.Id);

        Assert.Single(await store.GetAllAsync());
    }

    [Fact]
    public async Task Changing_a_password_invalidates_the_old_one()
    {
        var (service, _) = Build();
        var user = await service.CreateAsync("matt", "Matt R", "hunter2");

        await service.SetPasswordAsync(user.Id, "hunter3");

        Assert.Null(await service.AuthenticateAsync("matt", "hunter2"));
        Assert.NotNull(await service.AuthenticateAsync("matt", "hunter3"));
    }

    private static (UserService Service, InMemoryStore<User> Store) Build()
    {
        var store = new InMemoryStore<User>();
        return (new UserService(store, NullLogger<UserService>.Instance), store);
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
}
