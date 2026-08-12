using DevStudio.Application.Repositories;

namespace DevStudio.Tests;

public class LocalRepositoryPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devstudio-local-" + Guid.NewGuid().ToString("n"));

    public LocalRepositoryPathsTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Roots_are_absolute_and_de_duplicated()
    {
        var roots = LocalRepositoryPaths.NormaliseRoots([_root, _root + Path.DirectorySeparatorChar, "  ", null!]);

        Assert.Single(roots);
        Assert.Equal(Path.GetFullPath(_root), roots[0]);
    }

    [Fact]
    public void A_path_inside_a_root_resolves()
    {
        var inside = Path.Combine(_root, "repo");
        Directory.CreateDirectory(inside);

        Assert.True(LocalRepositoryPaths.TryResolveWithinRoots(inside, [_root], out var resolved));
        Assert.Equal(Path.GetFullPath(inside), resolved);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../../etc")]
    [InlineData("repo/../../elsewhere")]
    public void Traversal_out_of_a_root_is_refused(string relative)
    {
        var attempt = Path.Combine(_root, relative);

        Assert.False(LocalRepositoryPaths.TryResolveWithinRoots(attempt, [_root], out _));
    }

    [Fact]
    public void A_sibling_whose_name_starts_with_the_root_is_not_inside_it()
    {
        // "/host/repos-secret" must not pass a naive prefix check against "/host/repos".
        Assert.False(LocalRepositoryPaths.TryResolveWithinRoots(_root + "-secret", [_root], out _));
    }

    [Fact]
    public void Nothing_resolves_when_no_root_is_configured()
    {
        Assert.False(LocalRepositoryPaths.TryResolveWithinRoots(_root, [], out _));
        Assert.False(LocalRepositoryPaths.TryResolveWithinRoots(_root, null, out _));
    }

    [Fact]
    public void A_checkout_is_recognised_by_its_git_directory()
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));

        Assert.True(LocalRepositoryPaths.IsGitRepository(repo));
        Assert.False(LocalRepositoryPaths.IsGitRepository(_root));
    }

    [Fact]
    public void A_worktree_is_recognised_even_though_its_git_is_a_file()
    {
        var worktree = Path.Combine(_root, "worktree");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: /elsewhere/.git/worktrees/x");

        Assert.True(LocalRepositoryPaths.IsGitRepository(worktree));
    }

    [Fact]
    public void Volume_clones_keep_the_shared_worktrees_directory()
    {
        var path = LocalRepositoryPaths.WorktreeRoot(false, "/data/repos/app", "/data/worktrees", ".devstudio-worktrees");

        Assert.Equal("/data/worktrees", path);
    }

    [Fact]
    public void A_mounted_repo_cuts_worktrees_beside_itself_so_the_IDE_can_see_them()
    {
        var path = LocalRepositoryPaths.WorktreeRoot(true, "/host/repos/app", "/data/worktrees", ".devstudio-worktrees");

        Assert.Equal(Path.GetFullPath("/host/repos/.devstudio-worktrees"), Path.GetFullPath(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
