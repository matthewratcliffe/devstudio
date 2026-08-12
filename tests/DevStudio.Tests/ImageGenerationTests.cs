using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Images;
using DevStudio.Infrastructure.Images;
using DevStudio.Infrastructure.Persistence;
using DevStudio.Infrastructure.Processes;
using DevStudio.Infrastructure.Providers.OpenAi;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevStudio.Tests;

/// <summary>
/// The image layer, driven through a stub backend. What matters here is everything around the HTTP
/// call — where the bytes land, what gets recorded, and which requests are refused before a free
/// quota is spent on them.
/// </summary>
public class ImageGenerationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devstudio-images-" + Guid.NewGuid().ToString("n"));

    private readonly StubGenerator _pollinations = new(ImageBackend.Pollinations, supportsInput: false);
    private readonly StubGenerator _gemini = new(ImageBackend.Gemini, supportsInput: true);
    private readonly StubSettings _settings = new();

    private ImageGenerationService CreateService() => new(
        [_pollinations, _gemini],
        new JsonEntityStore<GeneratedImage>(
            Options.Create(new OrchestratorOptions { DataPath = _root }),
            NullLogger<JsonEntityStore<GeneratedImage>>.Instance),
        _settings,
        Options.Create(new OrchestratorOptions { DataPath = _root }),
        NullLogger<ImageGenerationService>.Instance);

    [Fact]
    public async Task Writes_the_bytes_and_records_how_they_were_made()
    {
        var service = CreateService();

        var image = await service.GenerateAsync(new ImageRequest { Prompt = "a red cube", Seed = 42 });

        Assert.True(File.Exists(Path.Combine(service.GetImagesPath(), image.FileName)));
        Assert.Equal(ImageBackend.Pollinations, image.Backend);
        Assert.Equal("stub-model", image.Model);
        Assert.Equal("a red cube", image.Prompt);
        Assert.Equal(42, image.Seed);
        Assert.EndsWith(".jpg", image.FileName);

        // The record has to survive on its own — the gallery reads it back, not the file.
        var all = await service.GetAllAsync();
        Assert.Single(all);
    }

    [Fact]
    public async Task Serves_images_from_a_url_that_carries_no_path()
    {
        var service = CreateService();
        var image = await service.GenerateAsync(new ImageRequest { Prompt = "anything" });

        Assert.Equal($"/images/{image.FileName}", service.UrlFor(image));
        Assert.DoesNotContain('/', image.FileName);
    }

    [Fact]
    public async Task Clamps_dimensions_to_the_configured_maximum()
    {
        _settings.Current.MaxDimension = 1024;
        var service = CreateService();

        var image = await service.GenerateAsync(new ImageRequest { Prompt = "huge", Width = 8000, Height = 8000 });

        Assert.Equal(1024, image.Width);
        Assert.Equal(1024, _pollinations.LastRequest!.Width);
    }

    [Fact]
    public async Task Uses_the_default_backend_when_none_is_named()
    {
        _settings.Current.DefaultBackend = ImageBackend.Gemini;
        var service = CreateService();

        var image = await service.GenerateAsync(new ImageRequest { Prompt = "whichever" });

        Assert.Equal(ImageBackend.Gemini, image.Backend);
        Assert.Null(_pollinations.LastRequest);
    }

    [Fact]
    public async Task Refuses_an_edit_on_a_backend_that_cannot_edit()
    {
        var service = CreateService();

        var request = new ImageRequest
        {
            Prompt = "make the sky darker",
            Input = new ImageInput([1, 2, 3], "image/png"),
        };

        // Silently dropping the input would return an unrelated picture and look like success.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GenerateAsync(request, ImageBackend.Pollinations));

        Assert.Contains("cannot edit", error.Message);
        Assert.Null(_pollinations.LastRequest);
    }

    [Fact]
    public async Task Passes_an_edit_through_to_a_backend_that_can()
    {
        var service = CreateService();

        await service.GenerateAsync(
            new ImageRequest { Prompt = "make the sky darker", Input = new ImageInput([1, 2, 3], "image/png") },
            ImageBackend.Gemini);

        Assert.NotNull(_gemini.LastRequest!.Input);
    }

    [Fact]
    public async Task Names_a_download_after_the_prompt()
    {
        var service = CreateService();
        var image = await service.GenerateAsync(new ImageRequest { Prompt = "A fluffy ginger cat, sitting!" });

        Assert.Equal("a-fluffy-ginger-cat-sitting.jpg", service.DownloadNameFor(image));
    }

    [Theory]
    [InlineData("", "image.jpg")]
    [InlineData("../../etc/passwd", "etcpasswd.jpg")]
    [InlineData("!!! ???", "image.jpg")]
    public void Reduces_a_prompt_to_something_a_filesystem_will_accept(string prompt, string expected)
    {
        var image = new GeneratedImage { Prompt = prompt, FileName = "abc.jpg" };

        Assert.Equal(expected, CreateService().DownloadNameFor(image));
    }

    [Fact]
    public async Task Finds_the_record_behind_a_served_file()
    {
        var service = CreateService();
        var image = await service.GenerateAsync(new ImageRequest { Prompt = "findable" });

        Assert.Equal(image.Id, (await service.FindByFileNameAsync(image.FileName))?.Id);
        Assert.Null(await service.FindByFileNameAsync("not-a-file.jpg"));
    }

    [Fact]
    public async Task Delete_removes_the_record_and_the_file()
    {
        var service = CreateService();
        var image = await service.GenerateAsync(new ImageRequest { Prompt = "temporary" });
        var path = Path.Combine(service.GetImagesPath(), image.FileName);

        Assert.True(await service.DeleteAsync(image.Id));

        Assert.False(File.Exists(path));
        Assert.Empty(await service.GetAllAsync());
    }

    [Fact]
    public async Task Rereads_settings_before_every_generation()
    {
        var service = CreateService();
        await service.GenerateAsync(new ImageRequest { Prompt = "first" });

        // Standing in for a key saved on the Logins page while a session is already running.
        _settings.Current.DefaultBackend = ImageBackend.Gemini;

        var second = await service.GenerateAsync(new ImageRequest { Prompt = "second" });

        Assert.Equal(ImageBackend.Gemini, second.Backend);
        Assert.Equal(2, _settings.Loads);
    }

    [Fact]
    public void Tool_is_not_advertised_when_nothing_is_configured()
    {
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);

        var without = new WorkspaceTools(_root, PermissionMode.AcceptEdits, runner);
        Assert.DoesNotContain("generate_image", Names(without));

        var with = new WorkspaceTools(_root, PermissionMode.AcceptEdits, runner, CreateService());
        Assert.Contains("generate_image", Names(with));
    }

    [Fact]
    public async Task Tool_returns_the_image_for_the_transcript_as_well_as_the_model()
    {
        Directory.CreateDirectory(_root);
        var service = CreateService();

        var tools = new WorkspaceTools(_root, PermissionMode.AcceptEdits, new ProcessRunner(NullLogger<ProcessRunner>.Instance), service, "session-1");
        var outcome = await tools.InvokeAsync("generate_image", """{"prompt":"a red cube"}""", CancellationToken.None);

        // The markdown is what puts the picture in front of the operator whether or not the model
        // bothers to mention it.
        Assert.NotNull(outcome.ForTranscript);
        Assert.StartsWith("![a red cube](/images/", outcome.ForTranscript);
        Assert.Contains("/images/", outcome.ForModel);

        // Written into the workspace too, so the agent can go on to use the file.
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_root, "generated-images")));

        var image = Assert.Single(await service.GetAllAsync());
        Assert.Equal("session-1", image.SessionId);
    }

    [Fact]
    public async Task Tool_reports_a_backend_failure_instead_of_ending_the_turn()
    {
        _pollinations.Fails = true;
        var tools = new WorkspaceTools(_root, PermissionMode.AcceptEdits, new ProcessRunner(NullLogger<ProcessRunner>.Instance), CreateService());

        var outcome = await tools.InvokeAsync("generate_image", """{"prompt":"a red cube"}""", CancellationToken.None);

        Assert.StartsWith("Error:", outcome.ForModel);
        Assert.Null(outcome.ForTranscript);
    }

    private static IEnumerable<string?> Names(WorkspaceTools tools) =>
        tools.Definitions().Select(t => t?["function"]?["name"]?.GetValue<string>());

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }

    private sealed class StubGenerator(ImageBackend backend, bool supportsInput) : IImageGenerator
    {
        public ImageBackend Backend => backend;
        public string DisplayName => backend.ToString();
        public bool SupportsImageInput => supportsInput;
        public IReadOnlyList<string> Models => ["stub-model"];

        public ImageRequest? LastRequest { get; private set; }
        public bool Fails { get; set; }

        public ImageAvailability Check() => new(true, "stub");

        public Task<ImageBytes> GenerateAsync(ImageRequest request, CancellationToken ct = default)
        {
            if (Fails)
                throw new InvalidOperationException("the backend said no");

            LastRequest = request;
            return Task.FromResult(new ImageBytes([0xFF, 0xD8, 0xFF], "image/jpeg", "stub-model"));
        }
    }

    private sealed class StubSettings : IImageSettingsService
    {
        public ImageSettings Current { get; } = new();

        public int Loads { get; private set; }

        public Task<ImageSettings> LoadAsync(CancellationToken ct = default)
        {
            Loads++;
            return Task.FromResult(Current);
        }

        public Task SaveAsync(ImageSettings settings, CancellationToken ct = default) => Task.CompletedTask;
    }
}
