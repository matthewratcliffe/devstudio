using DevStudio.Infrastructure.Workspaces;

namespace DevStudio.Tests;

public sealed class WorkspacePathGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devstudio-pathguard-" + Guid.NewGuid().ToString("n"));
    private readonly string _workspace;

    public WorkspacePathGuardTests()
    {
        _workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(_workspace);
    }

    [Fact]
    public void A_sibling_path_is_not_inside_the_workspace()
    {
        var sibling = Path.Combine(_root, "workspace-other", "secret.txt");

        Assert.False(WorkspacePathGuard.TryResolveWithin(_workspace, sibling, out _));
    }

    [Fact]
    public void A_relative_parent_path_is_not_inside_the_workspace()
    {
        Assert.False(WorkspacePathGuard.TryResolveWithin(_workspace, "..\\workspace-other\\secret.txt", out _));
    }

    [Fact]
    public void Validation_can_be_explicitly_disabled()
    {
        var sibling = Path.Combine(_root, "workspace-other", "secret.txt");

        Assert.True(WorkspacePathGuard.TryResolveWithin(
            _workspace,
            sibling,
            out var resolved,
            validatePaths: false));
        Assert.Equal(Path.GetFullPath(sibling), resolved);
    }

    [Fact]
    public void An_existing_symlink_outside_the_workspace_is_rejected()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");

        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_workspace, "linked"), outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return;
        }

        Assert.False(WorkspacePathGuard.TryResolveWithin(_workspace, "linked/secret.txt", out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }
}
