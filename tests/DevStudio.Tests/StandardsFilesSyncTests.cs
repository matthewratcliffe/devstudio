using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Domain.Globals;
using DevStudio.Domain.Projects;
using DevStudio.Domain.Providers;
using DevStudio.Domain.Repositories;
using DevStudio.Infrastructure.Globals;
using DevStudio.Infrastructure.Persistence;
using DevStudio.Infrastructure.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevStudio.Tests;

/// <summary>
/// The sync runs on a timer and again before every session starts, so what it does when nothing has
/// changed matters as much as what it does when something has.
/// </summary>
public sealed class StandardsFilesSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devstudio-standards-" + Guid.NewGuid().ToString("n"));
    private readonly string _checkout;
    private readonly StandardsFilesSyncService _sync;
    private readonly JsonEntityStore<GlobalSettings> _globals;
    private readonly string _library;

    public StandardsFilesSyncTests()
    {
        var options = Options.Create(new OrchestratorOptions { DataPath = _root });
        _checkout = Path.Combine(_root, "checkout");
        Directory.CreateDirectory(_checkout);

        _globals = new JsonEntityStore<GlobalSettings>(options, NullLogger<JsonEntityStore<GlobalSettings>>.Instance);
        var repositories = new JsonEntityStore<GitRepository>(options, NullLogger<JsonEntityStore<GitRepository>>.Instance);

        var files = new FileLibraryService(
            new JsonEntityStore<Project>(options, NullLogger<JsonEntityStore<Project>>.Instance),
            _globals,
            options);

        _library = files.GetFilesPath(FileScope.Global);

        var repository = repositories
            .UpsertAsync(new GitRepository { Name = "standards", LocalPath = _checkout })
            .GetAwaiter().GetResult();

        _globals.UpsertAsync(new GlobalSettings
        {
            Id = GlobalSettings.WellKnownId,
            FilesRepositoryId = repository.Id,
            FilesPullBeforeSync = false,
        }).GetAwaiter().GetResult();

        _sync = new StandardsFilesSyncService(
            _globals,
            repositories,
            files,
            new StubGit(),
            NullLogger<StandardsFilesSyncService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void WriteStandard(string name, string content) =>
        File.WriteAllText(Path.Combine(_checkout, name), content);

    [Fact]
    public async Task The_first_sync_imports_everything()
    {
        WriteStandard("naming.md", "name things well");
        WriteStandard("testing.md", "test things");

        var result = await _sync.SyncAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Imported);
        Assert.Equal("name things well", await File.ReadAllTextAsync(Path.Combine(_library, "naming.md")));
    }

    /// <summary>
    /// The one that was costing something. Rewriting every file on every run meant the published
    /// copy was being swapped constantly, and a session provisioning its workspace at that moment
    /// copied the library mid-change.
    /// </summary>
    [Fact]
    public async Task A_second_sync_with_nothing_changed_rewrites_nothing()
    {
        WriteStandard("naming.md", "name things well");
        await _sync.SyncAsync();

        var writtenAt = File.GetLastWriteTimeUtc(Path.Combine(_library, "naming.md"));
        await Task.Delay(20);

        var result = await _sync.SyncAsync();

        Assert.Equal(0, result.Imported);
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(Path.Combine(_library, "naming.md")));
    }

    /// <summary>"Nothing to import" used to cover this too, which read as though the files had gone.</summary>
    [Fact]
    public async Task An_unchanged_sync_says_it_is_up_to_date_rather_than_empty()
    {
        WriteStandard("naming.md", "name things well");
        await _sync.SyncAsync();

        var result = await _sync.SyncAsync();

        Assert.Contains("Up to date", result.Message);
        Assert.DoesNotContain("no files", result.Message);
    }

    [Fact]
    public async Task An_edited_file_is_re_imported()
    {
        WriteStandard("naming.md", "first");
        await _sync.SyncAsync();

        WriteStandard("naming.md", "second");
        var result = await _sync.SyncAsync();

        Assert.Equal(1, result.Imported);
        Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(_library, "naming.md")));
    }

    /// <summary>
    /// Same length, different bytes — the case a cheap length check alone would wave through.
    /// </summary>
    [Fact]
    public async Task An_edit_that_keeps_the_length_is_still_re_imported()
    {
        WriteStandard("naming.md", "aaaa");
        await _sync.SyncAsync();

        WriteStandard("naming.md", "bbbb");
        var result = await _sync.SyncAsync();

        Assert.Equal(1, result.Imported);
        Assert.Equal("bbbb", await File.ReadAllTextAsync(Path.Combine(_library, "naming.md")));
    }

    /// <summary>
    /// A checkout replaced on disk moves every timestamp without the content changing, which is why
    /// the comparison reads bytes rather than trusting mtimes.
    /// </summary>
    [Fact]
    public async Task A_touched_but_identical_file_is_not_re_imported()
    {
        WriteStandard("naming.md", "unchanged");
        await _sync.SyncAsync();

        File.SetLastWriteTimeUtc(Path.Combine(_checkout, "naming.md"), DateTime.UtcNow.AddHours(1));

        var result = await _sync.SyncAsync();

        Assert.Equal(0, result.Imported);
    }

    [Fact]
    public async Task A_file_deleted_from_the_repository_is_removed_here()
    {
        WriteStandard("naming.md", "one");
        WriteStandard("testing.md", "two");
        await _sync.SyncAsync();

        File.Delete(Path.Combine(_checkout, "testing.md"));
        var result = await _sync.SyncAsync();

        Assert.Equal(1, result.Removed);
        Assert.False(File.Exists(Path.Combine(_library, "testing.md")));
    }

    /// <summary>The published folder is copied into every workspace, so it must hold only real files.</summary>
    [Fact]
    public async Task Syncing_repeatedly_leaves_no_working_files_behind()
    {
        WriteStandard("naming.md", "one");
        WriteStandard("testing.md", "two");

        for (var i = 0; i < 5; i++)
        {
            WriteStandard("naming.md", $"revision {i}");
            await _sync.SyncAsync();
        }

        var published = Directory.EnumerateFiles(_library)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Order()
            .ToArray();

        Assert.Equal(["naming.md", "testing.md"], published);
    }

    private sealed class StubGit : IGitService
    {
        public IReadOnlyList<string> LocalRepositoryRoots => [];

        public Task<GitCommandOutcome> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandOutcome(true, string.Empty));

        public Task<LocalBrowseResult> BrowseLocalAsync(string? path, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<GitRepository> AttachLocalAsync(string path, string? name, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<GitRepository> RenameAsync(GitRepository repository, string? name, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<GitRepository> CloneAsync(string remoteUrl, string? name, SourceControlProvider sourceControl = SourceControlProvider.GitHub, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<GitCommandOutcome> FetchAsync(GitRepository repository, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListBranchesAsync(GitRepository repository, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string> GetStatusAsync(string workingDirectory, CancellationToken ct = default) =>
            Task.FromResult(string.Empty);

        public Task<Worktree> CreateWorktreeAsync(GitRepository repository, string branch, string? baseBranch, bool ephemeral, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<GitCommandOutcome> RemoveWorktreeAsync(GitRepository repository, Worktree worktree, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
