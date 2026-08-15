using System.Net;
using System.Security.Claims;
using System.Text;
using DevStudio.Application.Users;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;

namespace DevStudio.Ui.Endpoints;

/// <summary>
/// Sign-in and sign-out. Written as plain endpoints rendering their own HTML rather than as Razor
/// components, because the app renders every route interactively and a form that has to reach
/// <see cref="AuthenticationHttpContextExtensions.SignInAsync(HttpContext, ClaimsPrincipal)"/> needs
/// a real request — not a circuit. The two OAuth callbacks in Program.cs are built the same way.
/// </summary>
public static class AuthEndpoints
{
    /// <summary>Claim carrying the account id, so a page can tell which account it is looking at.</summary>
    public const string UserIdClaim = "devstudio:uid";

    public static IEndpointRouteBuilder MapAuth(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(string.Empty).AllowAnonymous();

        group.MapGet("/login", (HttpContext context, IAntiforgery antiforgery) =>
            LoginPage(context, antiforgery, context.Request.Query["returnUrl"], error: null));

        group.MapPost("/login", async (
            HttpContext context,
            IAntiforgery antiforgery,
            IUserService users,
            CancellationToken ct) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                // Almost always a form left open until the token expired, so say that rather than 400.
                return LoginPage(context, antiforgery, null, "That page went stale. Try again.");
            }

            var form = await context.Request.ReadFormAsync(ct);
            var returnUrl = form["returnUrl"].ToString();
            var user = await users.AuthenticateAsync(form["username"].ToString(), form["password"].ToString(), ct);

            if (user is null)
                return LoginPage(context, antiforgery, returnUrl, "That username and password did not match.");

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, user.Id),
                    new Claim(ClaimTypes.Name, string.IsNullOrWhiteSpace(user.Name) ? user.Username : user.Name),
                    new Claim(UserIdClaim, user.Id),
                ],
                CookieAuthenticationDefaults.AuthenticationScheme);

            await context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity),
                // Persistent so an installed home-screen app is not asked to sign in every time iOS
                // discards the web view, which it does aggressively.
                new AuthenticationProperties { IsPersistent = true });

            return Results.Redirect(SafeReturnUrl(returnUrl));
        });

        // GET rather than a posted form: the sign-out control lives in the interactive layout, where
        // rendering an antiforgery token is awkward. The worst a forged request achieves is signing
        // the operator out, which they can undo by signing back in.
        group.MapGet("/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        });

        return app;
    }

    /// <summary>
    /// Only ever redirect back to a path on this app. An absolute URL in the query would otherwise
    /// turn the sign-in page into an open redirect.
    /// </summary>
    private static string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl)
        && returnUrl.StartsWith('/')
        && !returnUrl.StartsWith("//", StringComparison.Ordinal)
            ? returnUrl
            : "/";

    private static IResult LoginPage(HttpContext context, IAntiforgery antiforgery, string? returnUrl, string? error)
    {
        var token = antiforgery.GetAndStoreTokens(context);
        var encodedReturn = WebUtility.HtmlEncode(SafeReturnUrl(returnUrl));

        var banner = error is null
            ? string.Empty
            : $"""<div class="auth-error">{WebUtility.HtmlEncode(error)}</div>""";

        // Double-braced holes: the raw string carries CSS, so single braces have to stay literal.
        var html = $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1.0, viewport-fit=cover" />
              <title>Sign in · devStudio</title>
              <link rel="stylesheet" href="/app.css" />
              <link rel="manifest" href="/manifest.webmanifest" />
              <meta name="theme-color" content="#05070f" />
              <meta name="apple-mobile-web-app-capable" content="yes" />
              <meta name="apple-mobile-web-app-status-bar-style" content="black-translucent" />
              <link rel="apple-touch-icon" href="/apple-touch-icon.png" />
              <link rel="icon" type="image/png" href="/favicon.png" />
              <style>
                .auth-wrap { min-height: 100vh; display: grid; place-items: center; padding: 2rem 1.2rem; }
                .auth-card { width: 100%; max-width: 24rem; }
                .auth-brand { display: flex; align-items: center; gap: 0.6rem; margin-bottom: 1.4rem; }
                .auth-error {
                  border: 1px solid var(--red, #f56565); border-radius: 6px; padding: 0.6rem 0.8rem;
                  margin-bottom: 1rem; font-size: 0.8rem; color: var(--text-0);
                  background: rgba(245, 101, 101, 0.12);
                }
                .auth-card .btn { width: 100%; justify-content: center; }
              </style>
            </head>
            <body>
              <div class="auth-wrap">
                <div class="auth-card card">
                  <div class="auth-brand">
                    <span class="brand-mark">&gt;_</span>
                    <span class="brand-name">devStudio<span>orchestrator</span></span>
                  </div>
                  {{banner}}
                  <form method="post" action="/login">
                    <input type="hidden" name="{{token.FormFieldName}}" value="{{token.RequestToken}}" />
                    <input type="hidden" name="returnUrl" value="{{encodedReturn}}" />
                    <div class="field">
                      <label for="username">Username</label>
                      <input id="username" name="username" type="text" autocomplete="username"
                             autocapitalize="none" autocorrect="off" spellcheck="false" required autofocus />
                    </div>
                    <div class="field">
                      <label for="password">Password</label>
                      <input id="password" name="password" type="password" autocomplete="current-password" required />
                    </div>
                    <button class="btn btn-primary" type="submit">Sign in</button>
                  </form>
                </div>
              </div>
            </body>
            </html>
            """;

        return Results.Content(html, "text/html", Encoding.UTF8, error is null ? 200 : 401);
    }
}
