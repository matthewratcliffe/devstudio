using DevStudio.Application.Common;
using DevStudio.Application.Remoting;
using DevStudio.Domain.Remoting;
using DevStudio.Infrastructure.Persistence;
using DevStudio.Infrastructure.Remoting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace DevStudio.Tests;

/// <summary>
/// The handshake, which is the only thing standing between anything that can reach the port and an
/// agent running commands on this filesystem. These are the rules that make the approval click mean
/// something.
/// </summary>
public sealed class RemotePairingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devstudio-pairing-" + Guid.NewGuid().ToString("n"));
    private readonly JsonEntityStore<RemoteAccessGrant> _grants;
    private readonly RemoteTokenIssuer _issuer;
    private readonly RemoteAccessService _access;

    public RemotePairingTests()
    {
        var options = Options.Create(new OrchestratorOptions { DataPath = _root });

        _grants = new JsonEntityStore<RemoteAccessGrant>(
            options,
            NullLogger<JsonEntityStore<RemoteAccessGrant>>.Instance);

        _issuer = new RemoteTokenIssuer(options);
        _access = new RemoteAccessService(_grants, _issuer, NullLogger<RemoteAccessService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static RemotePairingRequest Request(string instanceId = "instance-1") =>
        new(instanceId, "laptop", "LAPTOP-01", "1.0.0");

    [Fact]
    public async Task A_request_grants_nothing_until_somebody_approves_it()
    {
        var grant = await _access.LodgeAsync(Request(), "192.168.1.10");

        Assert.Equal(RemoteGrantStatus.Pending, grant.Status);
        Assert.Null(await _access.IssueTokenAsync(grant));
    }

    [Fact]
    public async Task Approving_issues_a_key_the_requester_can_collect()
    {
        var grant = await _access.LodgeAsync(Request(), "192.168.1.10");
        var approved = await _access.ApproveAsync(grant.Id);

        var token = await _access.IssueTokenAsync(approved);

        Assert.NotNull(token);
        Assert.Equal(RemoteGrantStatus.Approved, approved.Status);
    }

    [Fact]
    public async Task A_denied_request_never_produces_a_key()
    {
        var grant = await _access.LodgeAsync(Request(), "192.168.1.10");
        var denied = await _access.DenyAsync(grant.Id);

        Assert.Null(await _access.IssueTokenAsync(denied));
    }

    /// <summary>
    /// The point of revoking. The key itself is five years long and cannot be recalled once handed
    /// over, so withdrawing has to be something the far side's next call is measured against.
    /// </summary>
    [Fact]
    public async Task Revoking_stops_the_key_being_reissued_even_though_it_still_verifies()
    {
        var grant = await _access.LodgeAsync(Request(), "192.168.1.10");
        var approved = await _access.ApproveAsync(grant.Id);
        var token = await _access.IssueTokenAsync(approved);

        await _access.RevokeAsync(approved.Id);

        var stored = await _access.GetAsync(approved.Id);
        Assert.Equal(RemoteGrantStatus.Revoked, stored!.Status);
        Assert.Null(await _access.IssueTokenAsync(stored));

        // The signature is still perfectly good — which is exactly why the grant has to be checked
        // on every call rather than trusted because the token parses.
        Assert.True(await Verifies(token!));
    }

    [Fact]
    public async Task A_request_that_has_already_been_decided_cannot_be_approved_twice()
    {
        var grant = await _access.LodgeAsync(Request(), "192.168.1.10");
        await _access.ApproveAsync(grant.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _access.ApproveAsync(grant.Id));
    }

    /// <summary>
    /// Re-pairing after the far side lost its key should not leave a second grant behind that nobody
    /// can tell from the first — but it still has to be approved again.
    /// </summary>
    [Fact]
    public async Task Re_requesting_from_the_same_instance_reuses_its_grant_and_asks_again()
    {
        var first = await _access.LodgeAsync(Request(), "192.168.1.10");
        await _access.ApproveAsync(first.Id);

        var second = await _access.LodgeAsync(Request(), "192.168.1.10");

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(RemoteGrantStatus.Pending, second.Status);
        Assert.Single(await _access.GetAllAsync());
    }

    [Fact]
    public async Task Each_request_gets_its_own_verification_code()
    {
        var first = await _access.LodgeAsync(Request("a"), "192.168.1.10");
        var second = await _access.LodgeAsync(Request("b"), "192.168.1.11");

        Assert.Equal(6, first.VerificationCode.Length);
        Assert.NotEqual(first.VerificationCode, second.VerificationCode);
    }

    [Fact]
    public async Task An_approved_key_carries_the_grant_it_belongs_to()
    {
        var grant = await _access.LodgeAsync(Request(), "192.168.1.10");
        var approved = await _access.ApproveAsync(grant.Id);
        var token = await _access.IssueTokenAsync(approved);

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, Validation(_issuer));

        Assert.True(result.IsValid);
        Assert.Equal(approved.Id, result.Claims[RemoteTokenIssuer.GrantIdClaim]);
    }

    /// <summary>
    /// Deliberately long: this pairs two machines the same person owns, and the thing that ends it is
    /// revocation rather than the calendar.
    /// </summary>
    [Fact]
    public async Task The_key_lasts_five_years()
    {
        var grant = await _access.LodgeAsync(Request(), "192.168.1.10");
        var approved = await _access.ApproveAsync(grant.Id);

        _issuer.Issue(approved, out var expiresAt);

        Assert.InRange(
            expiresAt,
            DateTimeOffset.UtcNow.AddDays(365 * 5 - 2),
            DateTimeOffset.UtcNow.AddDays(365 * 5 + 2));
    }

    /// <summary>
    /// A key signed by a different installation must not open this one. The signing secret is
    /// generated per data volume, so this is what keeps two unrelated instances apart.
    /// </summary>
    [Fact]
    public async Task A_key_signed_by_another_instance_is_refused()
    {
        var grant = await _access.LodgeAsync(Request(), "192.168.1.10");
        var approved = await _access.ApproveAsync(grant.Id);

        var elsewhere = Path.Combine(_root, "other");
        var other = new RemoteTokenIssuer(Options.Create(new OrchestratorOptions { DataPath = elsewhere }));
        var foreignToken = other.Issue(approved, out _);

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(foreignToken, Validation(_issuer));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void The_signing_key_survives_a_restart()
    {
        var options = Options.Create(new OrchestratorOptions { DataPath = _root });

        var first = new RemoteTokenIssuer(options).SigningKey;
        var second = new RemoteTokenIssuer(options).SigningKey;

        Assert.Equal(first, second);
    }

    /// <summary>
    /// A pending request left open for a fortnight becomes something somebody approves without
    /// remembering what asked for it.
    /// </summary>
    [Fact]
    public async Task A_stale_request_cannot_be_approved()
    {
        var grant = await _access.LodgeAsync(Request(), "192.168.1.10");
        grant.CreatedAt = DateTimeOffset.UtcNow.AddHours(-1);
        await _grants.UpsertAsync(grant);

        Assert.True(grant.IsExpiredRequest);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _access.ApproveAsync(grant.Id));
    }

    private async Task<bool> Verifies(string token) =>
        (await new JsonWebTokenHandler().ValidateTokenAsync(token, Validation(_issuer))).IsValid;

    private static TokenValidationParameters Validation(IRemoteTokenIssuer issuer) => new()
    {
        ValidIssuer = issuer.Issuer,
        ValidAudience = issuer.Audience,
        IssuerSigningKey = new SymmetricSecurityKey(issuer.SigningKey),
        ClockSkew = TimeSpan.Zero,
    };
}
