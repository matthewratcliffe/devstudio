using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Domain.Providers;
using DevStudio.Domain.Repositories;
using DevStudio.Infrastructure.Git;
using DevStudio.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevStudio.Tests;

/// <summary>
/// Renaming a registration only relabels it: the checkout is often a bind mount an IDE has open, so
/// nothing on disk may move. Names still have to stay unique because they prefix worktree folders.
/// </summary>
public class RepositoryRenameTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devstudio-rename-" + Guid.NewGuid().ToString("n"));

    public RepositoryRenameTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task An_empty_name_falls_back_to_the_folder_the_checkout_lives_in()
    {
        var service = Service();
        var repository = await Register("old-label", Path.Combine(_root, "team", "actual-folder"));

        var renamed = await service.RenameAsync(repository, "   ");

        Assert.Equal("actual-folder", renamed.Name);
    }

    [Fact]
    public async Task A_trailing_separator_does_not_swallow_the_folder_name()
    {
        var service = Service();
        var repository = await Register("old-label", Path.Combine(_root, "actual-folder") + Path.DirectorySeparatorChar);

        var renamed = await service.RenameAsync(repository, null);

        Assert.Equal("actual-folder", renamed.Name);
    }

    [Fact]
    public async Task A_name_another_repository_already_uses_is_made_unique()
    {
        var service = Service();
        await Register("shop", Path.Combine(_root, "shop"));
        var other = await Register("other", Path.Combine(_root, "other"));

        var renamed = await service.RenameAsync(other, "Shop");

        Assert.Equal("shop-2", renamed.Name);
    }

    [Fact]
    public async Task Renaming_to_the_name_it_already_has_is_not_made_unique_against_itself()
    {
        var service = Service();
        var repository = await Register("shop", Path.Combine(_root, "shop"));

        var renamed = await service.RenameAsync(repository, "shop");

        Assert.Equal("shop", renamed.Name);
    }

    [Fact]
    public async Task The_checkout_path_is_left_alone()
    {
        var service = Service();
        var path = Path.Combine(_root, "on-the-mount");
        var repository = await Register("shop", path);

        var renamed = await service.RenameAsync(repository, "something-else");

        Assert.Equal(path, renamed.LocalPath);
    }

    private Task<GitRepository> Register(string name, string localPath) =>
        Store().UpsertAsync(new GitRepository { Name = name, LocalPath = localPath, IsLocal = true });

    private OrchestratorOptions OptionsValue => new() { DataPath = _root, LocalRepositoryRoots = [_root] };

    private JsonEntityStore<GitRepository>? _store;

    private JsonEntityStore<GitRepository> Store() => _store ??=
        new JsonEntityStore<GitRepository>(Options.Create(OptionsValue), NullLogger<JsonEntityStore<GitRepository>>.Instance);

    private GitService Service()
    {
        var options = Options.Create(OptionsValue);

        return new GitService(
            new StubRunner(),
            new StubForges(),
            new StubHosts(),
            Store(),
            options,
            NullLogger<GitService>.Instance);
    }

    /// <summary>Renaming never shells out, so nothing here should ever be called.</summary>
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
