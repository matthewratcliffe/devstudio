using DevStudio.Application.Abstractions;
using DevStudio.Domain.Images;

namespace DevStudio.Infrastructure.Images;

/// <summary>
/// Pollinations: the prompt goes in the URL and the image comes back as the response body. No
/// account is needed, which makes this the only backend that works on a fresh checkout.
///
/// The catch is the rate limit — one request every fifteen seconds while anonymous, five with a free
/// token — and it is enforced by rejection rather than queueing. So requests are spaced here instead,
/// which turns "a second image in the same turn fails" into "it waits a bit".
/// </summary>
public sealed class PollinationsImageGenerator : IImageGenerator
{
    private readonly IHttpClientFactory _clients;
    private readonly IImageSettingsService _settings;

    /// <summary>Serialises callers, so the spacing below actually holds when two turns overlap.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    public PollinationsImageGenerator(IHttpClientFactory clients, IImageSettingsService settings)
    {
        _clients = clients;
        _settings = settings;
    }

    public ImageBackend Backend => ImageBackend.Pollinations;
    public string DisplayName => "Pollinations";

    /// <summary>The URL form takes a prompt and nothing else.</summary>
    public bool SupportsImageInput => false;

    public IReadOnlyList<string> Models => _settings.Current.Pollinations.Models;

    /// <summary>
    /// Always usable. Anonymous is a worse experience rather than a broken one, so the detail says
    /// which of the two you are getting instead of reporting a problem.
    /// </summary>
    public ImageAvailability Check() => new(
        true,
        string.IsNullOrWhiteSpace(_settings.Current.Pollinations.ApiToken)
            ? "No token — anonymous tier, watermarked and heavily rate limited."
            : "Using a token: no watermark, and a shorter wait between images.");

    public async Task<ImageBytes> GenerateAsync(ImageRequest request, CancellationToken ct = default)
    {
        var settings = _settings.Current.Pollinations;
        var model = string.IsNullOrWhiteSpace(request.Model) ? settings.Model : request.Model;

        var query = new List<string>
        {
            $"width={request.Width}",
            $"height={request.Height}",
            $"model={Uri.EscapeDataString(model)}",

            // Both are honoured only for a registered token; harmless otherwise.
            "nologo=true",
            "private=true",
        };

        if (request.Seed is { } seed)
            query.Add($"seed={seed}");

        var url = $"{settings.BaseUrl.TrimEnd('/')}/prompt/{Uri.EscapeDataString(request.Prompt)}?{string.Join('&', query)}";

        await WaitForTurnAsync(settings.MinSecondsBetweenRequests, ct);

        try
        {
            using var client = CreateClient();
            using var response = await client.GetAsync(url, ct);

            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(ct);

                throw new InvalidOperationException(response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                    ? "Pollinations rate-limited the request. Register a free token at auth.pollinations.ai, or wait and try again."
                    : $"Pollinations answered {(int)response.StatusCode}: {Trim(detail)}");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);

            // A rate-limit or queue message can arrive with a 200 and an HTML body, which would
            // otherwise be written to disk and served as a broken image.
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Pollinations returned {contentType} rather than an image: {Trim(System.Text.Encoding.UTF8.GetString(bytes))}");

            return new ImageBytes(bytes, contentType, model);
        }
        finally
        {
            _lastRequest = DateTimeOffset.UtcNow;
            _gate.Release();
        }
    }

    /// <summary>
    /// Takes the gate and holds it until enough time has passed since the last request. The gate is
    /// released by the caller's finally, so the timestamp is written after the request finishes.
    /// </summary>
    private async Task WaitForTurnAsync(int minSeconds, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);

        try
        {
            var wait = TimeSpan.FromSeconds(Math.Max(0, minSeconds)) - (DateTimeOffset.UtcNow - _lastRequest);

            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    private HttpClient CreateClient()
    {
        var client = _clients.CreateClient(nameof(PollinationsImageGenerator));
        client.Timeout = TimeSpan.FromSeconds(_settings.Current.TimeoutSeconds);

        if (!string.IsNullOrWhiteSpace(_settings.Current.Pollinations.ApiToken))
        {
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.Current.Pollinations.ApiToken);
        }

        return client;
    }

    private static string Trim(string text, int limit = 300) =>
        text.Length <= limit ? text : text[..limit] + "…";
}
