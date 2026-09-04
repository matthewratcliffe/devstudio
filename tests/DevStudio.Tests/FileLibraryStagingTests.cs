using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Domain.Globals;
using DevStudio.Domain.Projects;
using DevStudio.Infrastructure.Persistence;
using DevStudio.Infrastructure.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevStudio.Tests;

/// <summary>
/// The library folder is copied wholesale into every agent workspace while it is being written to,
/// so what it contains mid-save is not an internal detail — it is what an agent ends up reading.
/// </summary>
public sealed class FileLibraryStagingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devstudio-library-" + Guid.NewGuid().ToString("n"));
    private readonly FileLibraryService _files;
    private readonly string _library;

    public FileLibraryStagingTests()
    {
        var options = Options.Create(new OrchestratorOptions { DataPath = _root });

        _files = new FileLibraryService(
            new JsonEntityStore<Project>(options, NullLogger<JsonEntityStore<Project>>.Instance),
            new JsonEntityStore<GlobalSettings>(options, NullLogger<JsonEntityStore<GlobalSettings>>.Instance),
            options);

        _library = _files.GetFilesPath(FileScope.Global);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private Task SaveAsync(string name, string content) =>
        _files.SaveAsync(FileScope.Global, name, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)), "text/markdown");

    private string[] LibraryFiles() =>
        Directory.EnumerateFiles(_library).Select(Path.GetFileName).OfType<string>().Order().ToArray();

    [Fact]
    public async Task A_saved_file_lands_in_the_library()
    {
        await SaveAsync("standards.md", "be careful");

        Assert.Equal(["standards.md"], LibraryFiles());
        Assert.Equal("be careful", await File.ReadAllTextAsync(Path.Combine(_library, "standards.md")));
    }

    /// <summary>
    /// The half-finished copy used to be written beside the published file, so a workspace being
    /// staged at that moment picked it up and an agent found "standards.md.a1b2….tmp" sitting in its
    /// reference material.
    /// </summary>
    [Fact]
    public async Task Overwriting_leaves_no_working_files_in_the_library()
    {
        await SaveAsync("standards.md", "first");
        await SaveAsync("standards.md", "second");

        Assert.Equal(["standards.md"], LibraryFiles());
        Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(_library, "standards.md")));
    }

    /// <summary>
    /// The one that actually cost something. The old two-step swap moved the file aside and then
    /// moved the new one in, so between those a reader saw the file simply missing — and a session
    /// provisioned in that window ran with the standards absent, reporting only a log warning.
    /// </summary>
    [Fact]
    public async Task A_reader_never_sees_the_file_missing_while_it_is_being_replaced()
    {
        await SaveAsync("standards.md", "first");

        using var stop = new CancellationTokenSource();
        var missing = 0;
        var strays = 0;
        var reads = 0;

        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                // Exactly what WorkspaceService.CopyLibraryAsync does: enumerate the folder and take
                // what is there.
                var present = Directory.EnumerateFiles(_library).Select(Path.GetFileName).ToArray();

                if (!present.Contains("standards.md"))
                    Interlocked.Increment(ref missing);

                if (present.Any(f => f!.EndsWith(".tmp") || f.EndsWith(".bak")))
                    Interlocked.Increment(ref strays);

                Interlocked.Increment(ref reads);
            }
        });

        var failures = 0;
        for (var i = 0; i < 150; i++)
        {
            try
            {
                await SaveAsync("standards.md", $"revision {i}");
            }
            catch
            {
                failures++;
            }
        }

        await stop.CancelAsync();
        await reader;

        Assert.True(reads > 0, "the reader never ran, so the test proved nothing");
        Assert.True(missing == 0, $"file was missing on {missing} of {reads} reads ({failures} saves failed)");
        Assert.True(strays == 0, $"working files were visible on {strays} of {reads} reads");
    }

    [Fact]
    public async Task The_working_folder_is_not_inside_the_published_one()
    {
        await SaveAsync("standards.md", "first");
        await SaveAsync("standards.md", "second");

        // Nested would be just as bad for anything that copies recursively, even though the staging
        // copy happens to enumerate only the top level today.
        Assert.Empty(Directory.EnumerateDirectories(_library));
    }

    [Fact]
    public async Task Several_files_saved_at_once_all_arrive()
    {
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => SaveAsync($"file-{i}.md", $"content {i}")));

        Assert.Equal(20, LibraryFiles().Length);

        foreach (var i in Enumerable.Range(0, 20))
            Assert.Equal($"content {i}", await File.ReadAllTextAsync(Path.Combine(_library, $"file-{i}.md")));
    }
}
