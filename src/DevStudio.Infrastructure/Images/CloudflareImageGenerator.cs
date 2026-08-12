using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using DevStudio.Application.Abstractions;
using DevStudio.Domain.Images;

namespace DevStudio.Infrastructure.Images;

/// <summary>
/// Workers AI. Ten thousand neurons a day come free on every plan, and FLUX.1 Schnell costs about
/// 4.8 of them per 512×512 tile — roughly five hundred 1024×1024 images a day for nothing, with no
/// per-request throttle. That combination is why this is the backend to move to once the keyless one
/// starts getting in the way.
/// </summary>
public sealed class CloudflareImageGenerator : IImageGenerator
{
    private readonly IHttpClientFactory _clients;
    private readonly IImageSettingsService _settings;

    public CloudflareImageGenerator(IHttpClientFactory clients, IImageSettingsService settings)
    {
        _clients = clients;
        _settings = settings;
    }

    public ImageBackend Backend => ImageBackend.Cloudflare;
    public string DisplayName => "Cloudflare Workers AI";
    public bool SupportsImageInput => false;
    public IReadOnlyList<string> Models => _settings.Current.Cloudflare.Models;

    public ImageAvailability Check()
    {
        var settings = _settings.Current.Cloudflare;

        if (string.IsNullOrWhiteSpace(settings.AccountId))
            return new ImageAvailability(false, "Add your Cloudflare account id on the Logins page.");

        return string.IsNullOrWhiteSpace(settings.ApiToken)
            ? new ImageAvailability(false, "Add a Cloudflare API token with the Workers AI permission on the Logins page.")
            : new ImageAvailability(true, "10,000 neurons a day are free, resetting at 00:00 UTC.");
    }

    public async Task<ImageBytes> GenerateAsync(ImageRequest request, CancellationToken ct = default)
    {
        var settings = _settings.Current.Cloudflare;

        if (Check() is { Configured: false } availability)
            throw new InvalidOperationException(availability.Detail);

        var model = string.IsNullOrWhiteSpace(request.Model) ? settings.Model : request.Model;

        var body = new JsonObject
        {
            ["prompt"] = request.Prompt,
            ["width"] = request.Width,
            ["height"] = request.Height,
        };

        if (request.Seed is { } seed)
            body["seed"] = seed;

        using var client = CreateClient();
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        var url = $"https://api.cloudflare.com/client/v4/accounts/{settings.AccountId}/ai/run/{model}";
        using var response = await client.PostAsync(url, content, ct);

        var payload = await response.Content.ReadAsByteArrayAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Cloudflare answered {(int)response.StatusCode}: {Describe(payload)}");

        // Two shapes, by model: the diffusion models stream the image back directly, while
        // flux-1-schnell wraps a base64 JPEG in Cloudflare's usual result envelope.
        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

        if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return new ImageBytes(payload, contentType, model);

        var json = JsonNode.Parse(Encoding.UTF8.GetString(payload)) as JsonObject
                   ?? throw new InvalidOperationException("Cloudflare returned something that was neither an image nor JSON.");

        if (json["success"]?.GetValue<bool>() == false)
            throw new InvalidOperationException($"Cloudflare rejected the request: {Describe(payload)}");

        var encoded = json["result"]?["image"]?.GetValue<string>()
                      ?? throw new InvalidOperationException($"Cloudflare returned no image: {Describe(payload)}");

        return new ImageBytes(Convert.FromBase64String(encoded), "image/jpeg", model);
    }

    private HttpClient CreateClient()
    {
        var client = _clients.CreateClient(nameof(CloudflareImageGenerator));
        client.Timeout = TimeSpan.FromSeconds(_settings.Current.TimeoutSeconds);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _settings.Current.Cloudflare.ApiToken);

        return client;
    }

    /// <summary>
    /// Cloudflare reports failures as a JSON errors array. Pulling the messages out gives a usable
    /// line — the raw envelope is mostly noise, and the useful part is easy to miss inside it.
    /// </summary>
    private static string Describe(byte[] payload)
    {
        var text = Encoding.UTF8.GetString(payload);

        try
        {
            if (JsonNode.Parse(text)?["errors"] is JsonArray errors && errors.Count > 0)
            {
                var messages = errors
                    .Select(e => e?["message"]?.GetValue<string>())
                    .Where(m => !string.IsNullOrWhiteSpace(m));

                return string.Join("; ", messages);
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Not JSON, so the body itself is the best description available.
        }

        return text.Length <= 300 ? text : text[..300] + "…";
    }
}
