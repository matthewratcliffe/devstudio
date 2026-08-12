using System.Text;
using System.Text.Json.Nodes;
using DevStudio.Application.Abstractions;
using DevStudio.Domain.Images;

namespace DevStudio.Infrastructure.Images;

/// <summary>
/// Gemini's image models, through the AI Studio endpoint. Alone among the three this one edits: an
/// input image is just another part alongside the prompt, so "make the sky darker" is the same call
/// as "draw a sky". Google does not publish the free-tier numbers — AI Studio shows the limits for a
/// given key — and the pro image model needs billing enabled before it will answer at all.
/// </summary>
public sealed class GeminiImageGenerator : IImageGenerator
{
    private readonly IHttpClientFactory _clients;
    private readonly IImageSettingsService _settings;

    public GeminiImageGenerator(IHttpClientFactory clients, IImageSettingsService settings)
    {
        _clients = clients;
        _settings = settings;
    }

    public ImageBackend Backend => ImageBackend.Gemini;
    public string DisplayName => "Gemini";
    public bool SupportsImageInput => true;
    public IReadOnlyList<string> Models => _settings.Current.Gemini.Models;

    public ImageAvailability Check() =>
        string.IsNullOrWhiteSpace(_settings.Current.Gemini.ApiKey)
            ? new ImageAvailability(false, "Add a Google AI Studio key on the Logins page.")
            : new ImageAvailability(true, "Free-tier limits vary by key and region — AI Studio shows yours.");

    public async Task<ImageBytes> GenerateAsync(ImageRequest request, CancellationToken ct = default)
    {
        if (Check() is { Configured: false } availability)
            throw new InvalidOperationException(availability.Detail);

        var settings = _settings.Current.Gemini;
        var model = string.IsNullOrWhiteSpace(request.Model) ? settings.Model : request.Model;

        var parts = new JsonArray { new JsonObject { ["text"] = request.Prompt } };

        // An input image turns this into an edit. It goes before nothing in particular — the model
        // reads the whole part list — but the prompt reads better first.
        if (request.Input is { } input)
        {
            parts.Add(new JsonObject
            {
                ["inline_data"] = new JsonObject
                {
                    ["mime_type"] = input.ContentType,
                    ["data"] = Convert.ToBase64String(input.Content),
                },
            });
        }

        var body = new JsonObject
        {
            ["contents"] = new JsonArray { new JsonObject { ["parts"] = parts } },
            ["generationConfig"] = new JsonObject
            {
                // Both modalities: the image models return commentary alongside the image, and
                // asking for IMAGE alone is rejected by some of them.
                ["responseModalities"] = new JsonArray { "TEXT", "IMAGE" },
            },
        };

        using var client = _clients.CreateClient(nameof(GeminiImageGenerator));
        client.Timeout = TimeSpan.FromSeconds(_settings.Current.TimeoutSeconds);

        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"{settings.BaseUrl.TrimEnd('/')}/v1beta/models/{model}:generateContent")
        {
            Content = content,
        };

        // The key goes in a header rather than the query string, so it stays out of request logs.
        message.Headers.Add("x-goog-api-key", settings.ApiKey);

        using var response = await client.SendAsync(message, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Gemini answered {(int)response.StatusCode}: {Describe(payload)}");

        var json = JsonNode.Parse(payload) as JsonObject
                   ?? throw new InvalidOperationException("Gemini returned a body that was not JSON.");

        if (json["candidates"]?[0]?["content"]?["parts"] is not JsonArray responseParts)
            throw new InvalidOperationException($"Gemini returned no content: {Describe(payload)}");

        foreach (var part in responseParts.OfType<JsonObject>())
        {
            // Field naming differs between the REST shape and the SDK shape, and responses have used
            // both; checking each costs nothing and saves a confusing empty result.
            var inline = part["inlineData"] ?? part["inline_data"];

            if (inline?["data"]?.GetValue<string>() is not { Length: > 0 } encoded)
                continue;

            var mime = (inline["mimeType"] ?? inline["mime_type"])?.GetValue<string>() ?? "image/png";
            return new ImageBytes(Convert.FromBase64String(encoded), mime, model);
        }

        // Every part was text. Usually a refusal or a safety block, and the text explains which.
        var commentary = responseParts
            .Select(p => p?["text"]?.GetValue<string>())
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));

        throw new InvalidOperationException(string.IsNullOrWhiteSpace(commentary)
            ? $"Gemini returned no image: {Describe(payload)}"
            : $"Gemini returned no image: {commentary}");
    }

    private static string Describe(string payload)
    {
        try
        {
            if (JsonNode.Parse(payload)?["error"]?["message"]?.GetValue<string>() is { Length: > 0 } message)
                return message;
        }
        catch (System.Text.Json.JsonException)
        {
            // Fall through to the raw body.
        }

        return payload.Length <= 300 ? payload : payload[..300] + "…";
    }
}
