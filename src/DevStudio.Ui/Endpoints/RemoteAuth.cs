using DevStudio.Application.Remoting;
using DevStudio.Domain.Remoting;
using DevStudio.Infrastructure.Remoting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace DevStudio.Ui.Endpoints;

/// <summary>
/// The second way in to this app. People sign in with a cookie; other instances present a key they
/// were granted, and that key opens only the remote hub and the pairing endpoints — never a page,
/// and never the MCP surface.
/// </summary>
public static class RemoteAuth
{
    public const string Scheme = "RemoteInstance";

    /// <summary>
    /// Requires a valid key *and* a grant that is still approved. The second half is the important
    /// one: the key is good for five years and cannot be recalled once handed over, so revocation
    /// has to be a check made on every call rather than something baked into the token.
    /// </summary>
    public const string Policy = "RemoteInstance";

    public static IServiceCollection AddRemoteInstanceAuth(this IServiceCollection services)
    {
        services.AddAuthentication().AddJwtBearer(Scheme);

        // Configured this way, rather than inline above, so the signing key is read when the first
        // token is checked instead of at startup — it is generated on first use, which may well be
        // later than this.
        services.AddOptions<JwtBearerOptions>(Scheme)
            .Configure<IRemoteTokenIssuer>((options, issuer) =>
            {
                options.MapInboundClaims = false;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = issuer.Issuer,
                    ValidateAudience = true,
                    ValidAudience = issuer.Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(issuer.SigningKey),

                    // No allowance. Both machines are on the same network and keep their own time;
                    // five minutes of skew grace on a five-year token buys nothing.
                    ClockSkew = TimeSpan.Zero,
                };

                options.Events = new JwtBearerEvents
                {
                    // SignalR's WebSocket transport cannot set an Authorization header, so on that
                    // leg the key arrives in the query string. Accepted only for the hub path, so a
                    // key cannot be put in a link to anything else.
                    OnMessageReceived = context =>
                    {
                        var token = context.Request.Query["access_token"];

                        if (!string.IsNullOrEmpty(token) &&
                            context.HttpContext.Request.Path.StartsWithSegments(RemoteHubMethods.Path))
                        {
                            context.Token = token;
                        }

                        return Task.CompletedTask;
                    },

                    OnTokenValidated = async context =>
                    {
                        var grantId = context.Principal?.FindFirst(RemoteTokenIssuer.GrantIdClaim)?.Value;

                        if (string.IsNullOrWhiteSpace(grantId))
                        {
                            context.Fail("That key does not say which grant it belongs to.");
                            return;
                        }

                        var access = context.HttpContext.RequestServices.GetRequiredService<IRemoteAccessService>();
                        var grant = await access.GetAsync(grantId, context.HttpContext.RequestAborted);

                        // Withdrawn or deleted since the key was issued. This is the check that makes
                        // Revoke on the instances page mean anything.
                        if (grant is null || grant.Status != RemoteGrantStatus.Approved)
                            context.Fail("That instance's access has been withdrawn.");
                    },
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(Policy, policy => policy
                .AddAuthenticationSchemes(Scheme)
                .RequireAuthenticatedUser()
                .RequireClaim(RemoteTokenIssuer.GrantIdClaim));

        return services;
    }
}
