using System.Security.Cryptography;
using DevStudio.Application.Common;
using DevStudio.Application.Remoting;
using DevStudio.Domain.Remoting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace DevStudio.Infrastructure.Remoting;

/// <summary>
/// Signs the keys this instance hands to paired ones, and holds the secret it signs them with.
///
/// The secret is a random 512-bit value on the data volume beside the MCP token, generated the
/// first time a key is issued. Symmetric rather than a key pair because both signing and checking
/// happen here: a key this instance issued is only ever presented back to this instance, and the
/// paired side treats it as an opaque string it was given.
///
/// Deleting the file invalidates every key at once, which is the blunt instrument for a volume that
/// has leaked. The precise one — revoking a single instance — is a status change on its grant, which
/// is checked on every call.
/// </summary>
public sealed class RemoteTokenIssuer : IRemoteTokenIssuer
{
    private const string FileName = "remote-signing-key";

    /// <summary>
    /// Five years. This is a pairing between two machines the same person owns, and the thing that
    /// ends it is revocation, not the calendar. A short expiry here would buy nothing — nobody
    /// re-reads an approval prompt they have seen twelve times — while guaranteeing that remote work
    /// breaks one morning for a reason nobody remembers.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(365 * 5);

    public const string GrantIdClaim = "devstudio:grant";
    public const string InstanceNameClaim = "devstudio:instance";

    private readonly string _path;
    private readonly Lock _gate = new();
    private byte[]? _key;

    public RemoteTokenIssuer(IOptions<OrchestratorOptions> options)
    {
        _path = Path.Combine(options.Value.DataPath, FileName);
    }

    public string Issuer => "devstudio";
    public string Audience => "devstudio-remote";

    public byte[] SigningKey
    {
        get
        {
            lock (_gate)
            {
                if (_key is not null)
                    return _key;

                if (File.Exists(_path))
                    return _key = Convert.FromBase64String(File.ReadAllText(_path).Trim());

                var generated = RandomNumberGenerator.GetBytes(64);
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

                // Written through a temp file for the same reason the entity store is: a half-written
                // key would lock every paired instance out with no way back but re-pairing.
                var temp = _path + ".tmp";
                File.WriteAllText(temp, Convert.ToBase64String(generated));
                File.Move(temp, _path, overwrite: true);

                return _key = generated;
            }
        }
    }

    public string Issue(RemoteAccessGrant grant, out DateTimeOffset expiresAt)
    {
        expiresAt = DateTimeOffset.UtcNow.Add(Lifetime);

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(SigningKey),
            SecurityAlgorithms.HmacSha256);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Expires = expiresAt.UtcDateTime,
            IssuedAt = DateTime.UtcNow,
            SigningCredentials = credentials,
            Claims = new Dictionary<string, object>
            {
                // The grant id is both subject and jti: one key per grant, so revoking the grant is
                // revoking the key, with nothing to keep in step.
                [JwtRegisteredClaimNames.Sub] = grant.Id,
                [JwtRegisteredClaimNames.Jti] = grant.Id,
                [GrantIdClaim] = grant.Id,
                [InstanceNameClaim] = grant.InstanceName,
            },
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
