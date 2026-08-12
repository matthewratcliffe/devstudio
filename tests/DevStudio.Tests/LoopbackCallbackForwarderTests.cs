using System.Net;
using DevStudio.Application.Common;
using DevStudio.Infrastructure.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevStudio.Tests;

public class LoopbackCallbackForwarderTests
{
    private static (LoopbackCallbackForwarder Forwarder, RecordingHandler Handler) Create(
        HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new RecordingHandler(status);
        var forwarder = new LoopbackCallbackForwarder(
            new StubClientFactory(handler),
            Options.Create(new OrchestratorOptions { CliCallbackPort = 1455 }),
            NullLogger<LoopbackCallbackForwarder>.Instance);

        return (forwarder, handler);
    }

    [Fact]
    public async Task Replays_the_path_and_query_to_the_loopback_listener()
    {
        var (forwarder, handler) = Create();

        var result = await forwarder.ForwardAsync(
            "http://localhost:1455/auth/callback?code=ac_abc123&state=xyz&scope=openid+profile");

        Assert.True(result.Succeeded);
        Assert.Equal("127.0.0.1", handler.LastRequest!.Host);
        Assert.Equal(1455, handler.LastRequest.Port);
        Assert.Equal("/auth/callback", handler.LastRequest.AbsolutePath);
        Assert.Contains("code=ac_abc123", handler.LastRequest.Query);
        Assert.Contains("state=xyz", handler.LastRequest.Query);
    }

    [Fact]
    public async Task A_pasted_url_cannot_redirect_the_request_to_another_host()
    {
        var (forwarder, handler) = Create();

        // Whatever host is pasted, the request must still go to loopback.
        await forwarder.ForwardAsync("https://evil.example.com:443/auth/callback?code=stolen");

        Assert.Equal("127.0.0.1", handler.LastRequest!.Host);
        Assert.Equal(1455, handler.LastRequest.Port);
        Assert.Equal(Uri.UriSchemeHttp, handler.LastRequest.Scheme);
    }

    [Fact]
    public async Task Accepts_just_the_path_and_query()
    {
        var (forwarder, handler) = Create();

        var result = await forwarder.ForwardAsync("/auth/callback?code=ac_abc123");

        Assert.True(result.Succeeded);
        Assert.Equal("/auth/callback", handler.LastRequest!.AbsolutePath);
        Assert.Contains("code=ac_abc123", handler.LastRequest.Query);
    }

    [Fact]
    public async Task Rejects_a_url_that_is_not_http()
    {
        var (forwarder, handler) = Create();

        var result = await forwarder.ForwardAsync("file:///etc/passwd?code=x");

        Assert.False(result.Succeeded);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task Rejects_input_that_is_not_a_callback()
    {
        var (forwarder, handler) = Create();

        var result = await forwarder.ForwardAsync("not a url at all");

        Assert.False(result.Succeeded);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task Reports_a_refused_connection_as_the_login_no_longer_running()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK) { Throw = new HttpRequestException("connection refused") };
        var forwarder = new LoopbackCallbackForwarder(
            new StubClientFactory(handler),
            Options.Create(new OrchestratorOptions { CliCallbackPort = 1455 }),
            NullLogger<LoopbackCallbackForwarder>.Instance);

        var result = await forwarder.ForwardAsync("/auth/callback?code=x");

        Assert.False(result.Succeeded);
        Assert.Contains("1455", result.Detail);
    }

    [Fact]
    public async Task Surfaces_an_error_status_from_the_cli()
    {
        var (forwarder, _) = Create(HttpStatusCode.BadRequest);

        var result = await forwarder.ForwardAsync("/auth/callback?code=stale");

        Assert.False(result.Succeeded);
        Assert.Contains("400", result.Detail);
    }

    private sealed class RecordingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public Uri? LastRequest { get; private set; }
        public HttpRequestException? Throw { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Throw is not null)
                throw Throw;

            LastRequest = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("ok") });
        }
    }

    private sealed class StubClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
