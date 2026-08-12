using System.Net;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevStudio.Infrastructure.Providers;

/// <summary>
/// Replays an OAuth callback to the CLI's loopback listener. The target host and port are fixed, and
/// only the path and query survive from whatever was pasted in — this must never become a way to
/// make the server fetch an arbitrary URL.
/// </summary>
public sealed class LoopbackCallbackForwarder : ILoopbackCallbackForwarder
{
    private readonly IHttpClientFactory _clients;
    private readonly OrchestratorOptions _options;
    private readonly ILogger<LoopbackCallbackForwarder> _logger;

    public LoopbackCallbackForwarder(
        IHttpClientFactory clients,
        IOptions<OrchestratorOptions> options,
        ILogger<LoopbackCallbackForwarder> logger)
    {
        _clients = clients;
        _options = options.Value;
        _logger = logger;
    }

    public int CallbackPort => _options.CliCallbackPort;

    public Task<CallbackResult> ForwardAsync(string callbackUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(callbackUrl))
            return Task.FromResult(new CallbackResult(false, "Paste the whole callback URL from the browser's address bar."));

        var trimmed = callbackUrl.Trim();

        // A leading slash is a path, so handle that before trying to parse a URL: on Unix
        // "/auth/callback?code=x" parses as an absolute file:// URI whose path swallows the query
        // as "%3Fcode=x", and the CLI would then be handed a callback with no code in it.
        if (trimmed.StartsWith('/'))
        {
            var split = trimmed.IndexOf('?');
            return ForwardAsync(
                split < 0 ? trimmed : trimmed[..split],
                split < 0 ? string.Empty : trimmed[split..],
                ct);
        }

        // Anything else has to be a real http(s) URL. The scheme check keeps file:// and friends out
        // even though only the path and query are ever used.
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return Task.FromResult(new CallbackResult(false, "That does not look like a callback URL."));
        }

        return ForwardAsync(uri.AbsolutePath, uri.Query, ct);
    }

    public async Task<CallbackResult> ForwardAsync(string path, string queryString, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/'))
            return new CallbackResult(false, "The callback path looks wrong.");

        // Fixed destination: the pasted URL contributes a path and query, nothing more.
        var target = new UriBuilder
        {
            Scheme = Uri.UriSchemeHttp,
            Host = IPAddress.Loopback.ToString(),
            Port = CallbackPort,
            Path = path,
            Query = queryString.TrimStart('?'),
        }.Uri;

        var client = _clients.CreateClient(nameof(LoopbackCallbackForwarder));
        client.Timeout = TimeSpan.FromSeconds(20);

        try
        {
            using var response = await client.GetAsync(target, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
                return new CallbackResult(true, "The CLI accepted the callback. Watch the terminal for confirmation.");

            _logger.LogWarning("Callback replay returned {Status}", response.StatusCode);
            return new CallbackResult(false, $"The CLI returned {(int)response.StatusCode}. {Summarise(body)}".Trim());
        }
        catch (HttpRequestException ex)
        {
            // Almost always means the login is no longer running, so nothing is listening.
            return new CallbackResult(false,
                $"Nothing is listening on port {CallbackPort}. Start the browser sign-in again, then retry. ({ex.Message})");
        }
        catch (TaskCanceledException)
        {
            return new CallbackResult(false, "The CLI did not answer in time.");
        }
    }

    private static string Summarise(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return string.Empty;

        var text = body.Length <= 200 ? body : body[..200];
        return text.Replace('\n', ' ').Replace('\r', ' ').Trim();
    }
}
