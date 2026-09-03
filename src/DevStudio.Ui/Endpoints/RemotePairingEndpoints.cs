using System.Reflection;
using DevStudio.Application.Remoting;
using DevStudio.Application.Notifications;
using DevStudio.Domain.Notifications;
using DevStudio.Domain.Remoting;

namespace DevStudio.Ui.Endpoints;

/// <summary>
/// The handshake, before there is any key. These three are the only endpoints another instance can
/// reach without one, and two of them can do nothing but lodge a request and read its own answer.
/// </summary>
public static class RemotePairingEndpoints
{
    public static IEndpointRouteBuilder MapRemotePairing(this IEndpointRouteBuilder app)
    {
        // Anonymous by necessity: an instance asking for access by definition has nothing to present
        // yet. All it can do is create a pending request that a person here has to act on.
        var open = app.MapGroup(string.Empty).AllowAnonymous();

        open.MapPost(RemotePairingRoutes.Request, async (
            RemotePairingRequest request,
            HttpContext context,
            IRemoteAccessService access,
            INotificationService notifications,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.InstanceId) || string.IsNullOrWhiteSpace(request.InstanceName))
                return Results.BadRequest(new { detail = "A pairing request needs an instance id and a name." });

            var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var grant = await access.LodgeAsync(request, address, ct);

            // The page showing pending requests is one somebody has to already be looking at.
            // A notification is what makes this work when they are not — you walk to the other
            // machine and it is waiting for you.
            await notifications.CreateAsync(
                "Remote access requested",
                $"{grant.InstanceName} ({grant.MachineName}) at {address} is asking to run work on this machine. "
                + $"Approve it under Remote instances if you recognise it — verification code {grant.VerificationCode}.",
                "warn",
                "remote",
                ct: ct);

            return Results.Ok(new RemotePairingResponse(
                grant.Id,
                grant.VerificationCode,
                "pending"));
        });

        open.MapGet(RemotePairingRoutes.Status, async (
            string requestId,
            IRemoteAccessService access,
            CancellationToken ct) =>
        {
            var grant = await access.GetAsync(requestId, ct);
            if (grant is null)
                return Results.NotFound();

            return grant.Status switch
            {
                RemoteGrantStatus.Approved => await Approved(grant, access, ct),

                RemoteGrantStatus.Denied or RemoteGrantStatus.Revoked => Results.Ok(new RemotePairingResponse(
                    grant.Id,
                    grant.VerificationCode,
                    "denied",
                    Detail: "The request was refused on the other machine.")),

                _ when grant.IsExpiredRequest => Results.Ok(new RemotePairingResponse(
                    grant.Id,
                    grant.VerificationCode,
                    "denied",
                    Detail: "The request expired before anybody approved it.")),

                _ => Results.Ok(new RemotePairingResponse(grant.Id, grant.VerificationCode, "pending")),
            };
        });

        // The only pre-key endpoint that is not part of pairing. Answers who this instance is to a
        // caller that already holds a key, which is what "Test connection" asks and what confirms a
        // revoke has taken effect.
        app.MapGet(RemotePairingRoutes.Hello, (HttpContext context) =>
        {
            var grantId = context.User.FindFirst(Infrastructure.Remoting.RemoteTokenIssuer.GrantIdClaim)?.Value;

            var expires = context.User.FindFirst("exp")?.Value is { } raw && long.TryParse(raw, out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : (DateTimeOffset?)null;

            return Results.Ok(new RemoteHelloResponse(
                Environment.MachineName,
                typeof(RemotePairingEndpoints).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                grantId ?? string.Empty,
                expires));
        }).RequireAuthorization(RemoteAuth.Policy);

        return app;
    }

    private static async Task<IResult> Approved(
        RemoteAccessGrant grant,
        IRemoteAccessService access,
        CancellationToken ct)
    {
        var token = await access.IssueTokenAsync(grant, ct);

        // Approved, but the collection window has closed — the requesting machine went away between
        // the click and the poll. Better to say so than to keep handing out keys indefinitely to
        // whoever knows the request id.
        return token is null
            ? Results.Ok(new RemotePairingResponse(
                grant.Id,
                grant.VerificationCode,
                "denied",
                Detail: "This was approved a while ago and the key was never collected. Request access again."))
            : Results.Ok(new RemotePairingResponse(
                grant.Id,
                grant.VerificationCode,
                "approved",
                token,
                grant.ExpiresAt,
                Environment.MachineName,
                typeof(RemotePairingEndpoints).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion));
    }
}
