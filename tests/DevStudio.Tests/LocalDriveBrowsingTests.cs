using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Application.Repositories;
using DevStudio.Domain.Common;
using DevStudio.Domain.Providers;
using DevStudio.Domain.Repositories;
using DevStudio.Infrastructure.Git;
using DevStudio.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevStudio.Tests;

/// <summary>
/// The desktop build runs as the user and has no container boundary, so the folder picker offers every
/// drive rather than only the mounts an operator declared. The allow-list is still the boundary — it is
/// just a wider one — and "up" has to stop where it ends.
/// </summary>
public class LocalDriveBrowsingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devstudio-drives-" + Guid.NewGuid().ToString("n"));
    private readonly string _nested;

    public LocalDriveBrowsingTests()
    {
        _nested = Path.Combine(_root, "team", "repo");
        Directory.CreateDirectory(_nested);
    }

    [Fact]
    public void Every_mounted_drive_is_offered_as_a_root()
    {
        var drives = LocalRepositoryPaths.DriveRoots();

        Assert.NotEmpty(drives);

        // Whatever the temp directory is on has to be reachable, or the picker cannot open anything.
        Assert.True(LocalRepositoryPaths.TryResolveWithinRoots(_root, drives, out _));
    }

    [Fact]
    public void Drives_are_only_offered_when_the_deployment_says_so()
    {
        Assert.Equal([_root], Service(allowDrives: false).LocalRepositoryRoots);
        Assert.Contains(_root, Service(allowDrives: true).LocalRepositoryRoots);
        Assert.True(Service(allowDrives: true).LocalRepositoryRoots.Count > 1);
    }

    [Fact]
    public async Task A_configured_mount_is_still_the_top_of_the_tree_without_drives()
    {
        var listing = await Service(allowDrives: false).BrowseLocalAsync(_root);

        Assert.Null(listing.ParentPath);
    }

    [Fact]
    public async Task With_drives_allowed_up_walks_out_of_a_mount()
    {
        var service = Service(allowDrives: true);

        var deep = await service.BrowseLocalAsync(_nested);
        Assert.Equal(Path.Combine(_root, "team"), deep.ParentPath);

        // The interesting case: the mount is no longer a ceiling, because its parent is inside a drive
        // that is allowed in its own right.
        var mount = await service.BrowseLocalAsync(_root);
        Assert.NotNull(mount.ParentPath);
    }

    [Fact]
    public async Task The_top_of_a_drive_has_nowhere_further_up()
    {
        var drive = Path.GetPathRoot(_root)!;

        var listing = await Service(allowDrives: true).BrowseLocalAsync(drive);

        Assert.Null(listing.ParentPath);
    }

    [Fact]
    public async Task A_drive_root_lists_without_tripping_over_folders_it_cannot_read()
    {
        // System Volume Information and friends live here and throw on enumeration. One of them used
        // to fail the whole listing.
        var listing = await Service(allowDrives: true).BrowseLocalAsync(Path.GetPathRoot(_root)!);

        Assert.NotNull(listing.Entries);
    }

    private GitService Service(bool allowDrives)
    {
        var options = Options.Create(new OrchestratorOptions
        {
            DataPath = _root,
            LocalRepositoryRoots = [_root],
            AllowAllLocalDrives = allowDrives,
        });

        return new GitService(
            new StubRunner(),
            new StubForges(),
            new StubHosts(),
            new JsonEntityStore<GitRepository>(options, NullLogger<JsonEntityStore<GitRepository>>.Instance),
            options,
            NullLogger<GitService>.Instance);
    }

    /// <summary>Browsing never shells out, so nothing here should ever be called.</summary>
    private sealed class StubRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<int> StreamAsync(
            ProcessRequest request,
            Func<string, bool, CancellationToken, Task> onLine,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class StubForges : ISourceControlRegistry
    {
        public IReadOnlyList<ISourceControlCli> All => [];

        public ISourceControlCli Get(SourceControlProvider provider) => throw new NotSupportedException();
    }

    private sealed class StubHosts : ISourceControlHosts
    {
        public string Get(SourceControlProvider provider) => "github.com";

        public bool IsOverridden(SourceControlProvider provider) => false;

        public Task SetAsync(SourceControlProvider provider, string? host, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }
}
