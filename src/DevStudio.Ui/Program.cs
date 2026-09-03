using DevStudio.Application;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Infrastructure;
using DevStudio.Ui.Components;
using DevStudio.Application.Remoting;
using DevStudio.Ui.Endpoints;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
    {
        // On everywhere, not just in development: a circuit that dies otherwise reports only that
        // something threw, which is no help at all when the thing that threw was on a background
        // render. This is a single-operator tool behind its own login, so the exception detail
        // reaching the browser is the operator's own.
        options.DetailedErrors = true;
    })
    .AddHubOptions(options =>
    {
        // Transcripts and project uploads travel over the circuit, so the default 32 KB is too small.
        options.MaximumReceiveMessageSize = 10 * 1024 * 1024;
    });

builder.Services.AddApplication();

// What the pickers on every page are built from, local or remote.
builder.Services.AddSingleton<DevStudio.Ui.Services.ExecutionTargets>();
builder.Services.AddInfrastructure(builder.Configuration);

// Sign-in cookies are encrypted with data protection keys, which default to a folder inside the
// container. Keeping them on the mounted volume instead means a redeploy does not sign everyone out.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(
        Path.GetFullPath(builder.Configuration.GetSection(OrchestratorOptions.SectionName)["DataPath"] ?? "/data"),
        "keys")))
    .SetApplicationName("devstudio");

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "devstudio_auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        // SameAsRequest, not Always: this is usually reached over plain HTTP on a home network, and
        // Always would issue a cookie the browser then refuses to send back.
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/login";
        options.ReturnUrlParameter = "returnUrl";
        options.AccessDeniedPath = "/login";
        // Deliberately long and sliding. The point is the installed home-screen app: iOS discards
        // the web view constantly, so anything shorter means signing in most times it is opened.
        // Sliding means the year only starts counting from the last visit. HttpOnly matters here too
        // — Safari caps cookies written by script at seven days, but not ones the server sets.
        options.ExpireTimeSpan = TimeSpan.FromDays(365);
        options.SlidingExpiration = true;
    });

// A second scheme, for other installations rather than people. Added after the cookie so the cookie
// stays the default and every page keeps authenticating exactly as it did.
builder.Services.AddRemoteInstanceAuth();

builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

// Friendly error pages are for browsers. Applied everywhere they would turn an empty 401 from the
// remote endpoints into a redirect to the sign-in page, and another instance — which is checking for
// exactly that 401 to notice its access was withdrawn — would follow it and get a page of HTML back
// instead of an answer.
app.UseWhen(
    context => !context.Request.Path.StartsWithSegments(RemoteHubMethods.Path) &&
               !context.Request.Path.StartsWithSegments("/remote"),
    branch => branch.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();

app.MapAuth();

// Every page needs an account. The sign-in endpoints opt out of this with AllowAnonymous; static
// assets are mapped separately and stay open, which is what lets the sign-in page style itself and
// the browser fetch the manifest before anyone has signed in.
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .RequireAuthorization();

// Agents talk to the orchestrator over MCP.
app.MapMcp();

// Other installations pair with this one over plain HTTP, then drive it over the hub. The hub is
// where a remote turn actually streams, which is what lets a conversation running on another machine
// fill in a transcript here as it happens rather than all at once when it finishes.
app.MapRemotePairing();
app.MapHub<RemoteHostHub>(RemoteHubMethods.Path);

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", time = DateTimeOffset.UtcNow }));

// The Codex browser sign-in redirects to http://localhost:1455/auth/callback, which only resolves on
// the container host. Accepting the same path here means a browser anywhere can finish the flow just
// by pointing at this app's port instead.
app.MapGet("/auth/callback", async (HttpContext context, ILoopbackCallbackForwarder forwarder, CancellationToken ct) =>
{
    var result = await forwarder.ForwardAsync("/auth/callback", context.Request.QueryString.Value ?? string.Empty, ct);

    return Results.Content(
        $"""
         <!doctype html>
         <html lang="en"><head><meta charset="utf-8"><title>Sign-in callback</title>
         <link rel="stylesheet" href="/app.css"></head>
         <body><div style="min-height:100vh;display:grid;place-items:center;text-align:center;padding:2rem"><div>
         <h1 class="text-gradient">{(result.Succeeded ? "Signed in" : "Callback failed")}</h1>
         <p class="muted">{System.Net.WebUtility.HtmlEncode(result.Detail)}</p>
         <a class="btn btn-primary" href="/providers">Back to logins</a>
         </div></div></body></html>
         """,
        "text/html",
        System.Text.Encoding.UTF8,
        result.Succeeded ? 200 : 502);
});

// Where an MCP server's OAuth sign-in comes back to. The redirect_uri is registered with the issuer
// when the flow starts, so this path is fixed; the state in the query is what identifies which
// server was being signed in to.
app.MapGet("/mcp/oauth/callback", async (HttpContext context, IMcpOAuthService oauth, CancellationToken ct) =>
{
    var query = context.Request.Query;

    var result = await oauth.CompleteAsync(
        query["state"],
        query["code"],
        query["error"],
        query["error_description"],
        ct);

    return Results.Content(
        $"""
         <!doctype html>
         <html lang="en"><head><meta charset="utf-8"><title>MCP sign-in</title>
         <link rel="stylesheet" href="/app.css"></head>
         <body><div style="min-height:100vh;display:grid;place-items:center;text-align:center;padding:2rem"><div>
         <h1 class="text-gradient">{(result.Succeeded ? "Signed in" : "Sign-in failed")}</h1>
         <p class="muted">{System.Net.WebUtility.HtmlEncode(result.Detail)}</p>
         <a class="btn btn-primary" href="/mcp-servers">Back to MCP servers</a>
         </div></div></body></html>
         """,
        "text/html",
        System.Text.Encoding.UTF8,
        result.Succeeded ? 200 : 400);
});

// Download an uploaded file. "global" is the shared library; anything else is a project id.
app.MapGet("/files/{scope}/{fileName}", (string scope, string fileName, IFileLibraryService files) =>
{
    var path = Path.Combine(files.GetFilesPath(FileScope.FromKey(scope)), Path.GetFileName(fileName));
    return File.Exists(path)
        ? Results.File(path, "application/octet-stream", Path.GetFileName(fileName))
        : Results.NotFound();
}).RequireAuthorization();

// Generated images, by the file name held on their record. Path.GetFileName keeps a crafted name
// from walking out of the folder, and the images are served inline because being looked at is the
// entire point of them.
app.MapGet("/images/{fileName}", async (
    string fileName,
    IImageGenerationService images,
    HttpContext context,
    CancellationToken ct) =>
{
    var safeName = Path.GetFileName(fileName);
    var path = Path.Combine(images.GetImagesPath(), safeName);

    if (!File.Exists(path))
        return Results.NotFound();

    var contentType = Path.GetExtension(safeName).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "image/jpeg",
    };

    if (!context.Request.Query.ContainsKey("download"))
        return Results.File(path, contentType);

    // Saved under its prompt rather than its id. Falling back to the stored name covers a file whose
    // record has been deleted from the gallery but which is still on disk.
    var record = await images.FindByFileNameAsync(safeName, ct);

    return Results.File(path, contentType, record is null ? safeName : images.DownloadNameFor(record));
}).RequireAuthorization();

// Anything an agent produced in its workspace, downloadable. The service refuses a path that
// resolves outside the session's own directory.
app.MapGet("/workspace/{sessionId}/{**path}", async (
    string sessionId,
    string path,
    IEntityStore<DevStudio.Domain.Sessions.ChatSession> sessions,
    IExecutionHostResolver hosts,
    HttpContext context,
    CancellationToken ct) =>
{
    // Opened on whichever machine the session ran on. A remote session's output never touches this
    // filesystem, so resolving the host is what makes the download link work at all.
    var session = await sessions.GetAsync(sessionId, ct);
    var host = await hosts.ResolveAsync(session?.RemoteInstanceId, ct);

    var file = await host.Files.OpenAsync(sessionId, path, ct);
    if (file is null)
        return Results.NotFound();

    // Images and PDFs are worth showing in place; everything else downloads.
    var inline = context.Request.Query.ContainsKey("inline");
    return inline
        ? Results.Stream(file.Value.Content, file.Value.ContentType)
        : Results.Stream(file.Value.Content, file.Value.ContentType, file.Value.FileName);
}).RequireAuthorization();

app.Run();
