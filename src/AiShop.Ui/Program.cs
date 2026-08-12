using AiShop.Application;
using AiShop.Application.Abstractions;
using AiShop.Infrastructure;
using AiShop.Ui.Components;
using AiShop.Ui.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(options =>
    {
        // Transcripts and project uploads travel over the circuit, so the default 32 KB is too small.
        options.MaximumReceiveMessageSize = 10 * 1024 * 1024;
    });

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();
app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Agents talk to the orchestrator over MCP.
app.MapMcp();

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

// Download an uploaded file. "global" is the shared library; anything else is a project id.
app.MapGet("/files/{scope}/{fileName}", (string scope, string fileName, IFileLibraryService files) =>
{
    var path = Path.Combine(files.GetFilesPath(FileScope.FromKey(scope)), Path.GetFileName(fileName));
    return File.Exists(path)
        ? Results.File(path, "application/octet-stream", Path.GetFileName(fileName))
        : Results.NotFound();
});

// Anything an agent produced in its workspace, downloadable. The service refuses a path that
// resolves outside the session's own directory.
app.MapGet("/workspace/{sessionId}/{**path}", async (
    string sessionId,
    string path,
    IWorkspaceFileService files,
    HttpContext context,
    CancellationToken ct) =>
{
    var file = await files.OpenAsync(sessionId, path, ct);
    if (file is null)
        return Results.NotFound();

    // Images and PDFs are worth showing in place; everything else downloads.
    var inline = context.Request.Query.ContainsKey("inline");
    return inline
        ? Results.Stream(file.Value.Content, file.Value.ContentType)
        : Results.Stream(file.Value.Content, file.Value.ContentType, file.Value.FileName);
});

app.Run();
