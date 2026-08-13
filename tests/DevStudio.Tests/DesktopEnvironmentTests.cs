using DevStudio.Desktop;

namespace DevStudio.Tests;

/// <summary>
/// The desktop build is the same server with a different configuration, and the configuration is
/// where its security posture lives. These are the values that differ from the container on purpose.
/// </summary>
public class DesktopEnvironmentTests
{
    [Fact]
    public void The_cli_sandboxes_are_left_on_because_there_is_no_container()
    {
        var environment = DesktopPaths.ServerEnvironment(7080);

        // The container sets this true because it is the boundary. On a desktop there is none, so
        // codex runs workspace-write instead of danger-full-access and claude keeps its bash sandbox.
        Assert.Equal("false", environment["Orchestrator__ContainerIsTheSandbox"]);
    }

    [Fact]
    public void The_real_home_is_used_so_existing_cli_logins_are_shared()
    {
        var environment = DesktopPaths.ServerEnvironment(7080);

        Assert.Equal(string.Empty, environment["Orchestrator__HomePath"]);
    }

    [Fact]
    public void The_shell_owns_updates_so_the_web_ui_does_not_also_nag()
    {
        var environment = DesktopPaths.ServerEnvironment(7080);

        Assert.Equal("false", environment["Orchestrator__UpdateCheckEnabled"]);
    }

    [Fact]
    public void The_chosen_port_reaches_the_mcp_server_url()
    {
        var environment = DesktopPaths.ServerEnvironment(51234);

        // Agents are handed this URL to call back on, so a fallback port has to propagate or the
        // orchestrator's own MCP server is advertised on a port nothing is listening to.
        Assert.Equal("51234", environment["Orchestrator__HttpPort"]);
        Assert.Equal("http://127.0.0.1:51234", environment["ASPNETCORE_URLS"]);
    }

    [Fact]
    public void Repositories_under_the_home_directory_are_attachable_without_any_mount()
    {
        var environment = DesktopPaths.ServerEnvironment(7080);

        Assert.Equal(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            environment["Orchestrator__LocalRepositoryRoots__0"]);
    }

    [Fact]
    public void Every_drive_is_browsable_because_checkouts_are_not_all_under_the_home_directory()
    {
        var environment = DesktopPaths.ServerEnvironment(7080);

        // The container leaves this off: there, the only reachable paths should be the bind mounts the
        // operator declared. A desktop install is already running as the user.
        Assert.Equal("true", environment["Orchestrator__AllowAllLocalDrives"]);
    }

    [Fact]
    public void State_is_kept_out_of_the_install_directory()
    {
        // Every update installs a new copy of the application and deletes the old one. State kept
        // beside the executable would go with it.
        Assert.DoesNotContain(Path.Combine("devStudio", "current"), DesktopPaths.DataRoot);
        Assert.StartsWith(DesktopPaths.DataRoot, DesktopPaths.WebViewProfile, StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_instance_is_refused_and_told_where_the_first_one_is()
    {
        using var first = SingleInstance.Acquire();
        Assert.True(first.Acquired);

        first.Publish("http://127.0.0.1:7080/");

        using var second = SingleInstance.Acquire();

        Assert.False(second.Acquired);
        Assert.Equal("http://127.0.0.1:7080/", second.RunningUrl);
    }

    [Fact]
    public void The_lock_is_released_when_the_first_instance_goes_away()
    {
        using (var first = SingleInstance.Acquire())
            Assert.True(first.Acquired);

        using var next = SingleInstance.Acquire();
        Assert.True(next.Acquired);
    }
}
