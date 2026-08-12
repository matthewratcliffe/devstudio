using System.Net;
using System.Text;
using DevStudio.Application.Common;
using DevStudio.Infrastructure.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevStudio.Tests;

/// <summary>
/// The container cannot update itself, so this is the only thing that tells anybody a newer image
/// exists. It has to be quiet when it should be — and never break the page when GitHub is not there.
/// </summary>
public class ReleaseCheckerTests
{
    private static GitHubReleaseChecker Checker(
        string? currentVersion,
        HttpResponseMessage response,
        OrchestratorOptions? options = null,
        Action<HttpRequestMessage>? onRequest = null) =>
        new(
            new HttpClient(new StubHandler(response, onRequest)),
            Options.Create(options ?? new OrchestratorOptions()),
            NullLogger<GitHubReleaseChecker>.Instance,
            currentVersion);

    private static HttpResponseMessage Release(string tag, bool draft = false, bool prerelease = false) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""
                  {
                    "tag_name": "{{tag}}",
                    "html_url": "https://github.com/matthewratcliffe/devstudio/releases/tag/{{tag}}",
                    "draft": {{draft.ToString().ToLowerInvariant()}},
                    "prerelease": {{prerelease.ToString().ToLowerInvariant()}}
                  }
                  """,
                Encoding.UTF8,
                "application/json"),
        };

    [Fact]
    public async Task A_newer_release_is_reported_with_somewhere_to_read_about_it()
    {
        var status = await Checker("1.4.2", Release("v1.5.0")).CheckAsync();

        Assert.True(status.UpdateAvailable);
        Assert.Equal("1.5.0", status.Latest);
        Assert.Equal("1.4.2", status.Current);
        Assert.Contains("releases/tag/v1.5.0", status.Url);
    }

    [Fact]
    public async Task The_same_version_reports_nothing()
    {
        var status = await Checker("1.5.0", Release("v1.5.0")).CheckAsync();

        Assert.False(status.UpdateAvailable);
        Assert.Equal("1.5.0", status.Current);
    }

    [Fact]
    public async Task A_pre_release_is_not_offered_to_a_stable_install()
    {
        var status = await Checker("1.4.2", Release("v1.5.0-rc.1", prerelease: true)).CheckAsync();

        Assert.False(status.UpdateAvailable);
    }

    [Fact]
    public async Task A_draft_release_is_not_a_release()
    {
        var status = await Checker("1.4.2", Release("v1.5.0", draft: true)).CheckAsync();

        Assert.False(status.UpdateAvailable);
    }

    [Fact]
    public async Task An_unversioned_build_never_asks_at_all()
    {
        var asked = false;

        var status = await Checker("1.0.0", Release("v1.5.0"), onRequest: _ => asked = true).CheckAsync();

        Assert.False(status.UpdateAvailable);
        Assert.False(asked);
    }

    [Fact]
    public async Task The_check_can_be_turned_off()
    {
        var asked = false;

        var checker = Checker(
            "1.4.2",
            Release("v1.5.0"),
            new OrchestratorOptions { UpdateCheckEnabled = false },
            _ => asked = true);

        Assert.False((await checker.CheckAsync()).UpdateAvailable);
        Assert.False(asked);
    }

    [Fact]
    public async Task GitHub_being_unreachable_is_not_an_error_in_the_UI()
    {
        var status = await Checker("1.4.2", new HttpResponseMessage(HttpStatusCode.NotFound)).CheckAsync();

        Assert.False(status.UpdateAvailable);
        Assert.Equal("1.4.2", status.Current);
    }

    [Fact]
    public async Task The_answer_is_cached_so_a_page_render_never_costs_a_request()
    {
        var requests = 0;
        var checker = Checker("1.4.2", Release("v1.5.0"), onRequest: _ => requests++);

        for (var i = 0; i < 5; i++)
            await checker.CheckAsync();

        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task The_configured_repository_is_the_one_asked_about()
    {
        Uri? asked = null;

        var checker = Checker(
            "1.4.2",
            Release("v1.5.0"),
            new OrchestratorOptions { UpdateRepository = "someone/fork" },
            request => asked = request.RequestUri);

        await checker.CheckAsync();

        Assert.Equal("https://api.github.com/repos/someone/fork/releases/latest", asked?.ToString());
    }

    private sealed class StubHandler(HttpResponseMessage response, Action<HttpRequestMessage>? onRequest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            onRequest?.Invoke(request);
            return Task.FromResult(response);
        }
    }
}
