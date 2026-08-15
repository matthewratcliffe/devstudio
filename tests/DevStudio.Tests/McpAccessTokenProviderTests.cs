using DevStudio.Application.Common;
using DevStudio.Infrastructure.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevStudio.Tests;

public class McpAccessTokenProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devstudio-mcptoken-" + Guid.NewGuid().ToString("n"));

    private McpAccessTokenProvider Create(int turnTimeoutMinutes = 60) => new(
        Options.Create(new OrchestratorOptions { DataPath = _root, TurnTimeoutMinutes = turnTimeoutMinutes }),
        NullLogger<McpAccessTokenProvider>.Instance);

    [Fact]
    public void A_token_is_issued_on_first_use_and_kept()
    {
        var provider = Create();

        var first = provider.Current;

        Assert.StartsWith("ds_", first);
        Assert.Equal(first, provider.Current);
    }

    [Fact]
    public void The_same_token_comes_back_after_a_restart()
    {
        var issued = Create().Current;

        Assert.Equal(issued, Create().Current);
    }

    [Fact]
    public void Only_the_current_token_matches()
    {
        var provider = Create();

        Assert.True(provider.Matches(provider.Current));
        Assert.False(provider.Matches("ds_not-it"));
        Assert.False(provider.Matches(provider.Current + "x"));
        Assert.False(provider.Matches(null));
        Assert.False(provider.Matches(string.Empty));
    }

    [Fact]
    public void Rotating_replaces_the_token_everywhere()
    {
        var provider = Create();
        var old = provider.Current;

        var rotated = provider.Rotate().Token;

        Assert.NotEqual(old, rotated);
        Assert.Equal(rotated, provider.Current);

        // And the new one is what a restart reads back, not the one it replaced.
        Assert.Equal(rotated, Create().Current);
    }

    [Fact]
    public void A_turn_already_in_flight_keeps_working_through_a_rotation()
    {
        var provider = Create();
        var inFlight = provider.Current;

        var rotation = provider.Rotate();

        // The CLI mid-turn has the old value in a file it has already read, so it has to keep being
        // accepted; the next turn rewrites .mcp.json with the new one on its own.
        Assert.True(provider.Matches(inFlight));
        Assert.True(provider.Matches(rotation.Token));
        Assert.NotNull(rotation.RetiredValidUntil);
    }

    [Fact]
    public void The_grace_window_outlasts_a_turn_but_not_by_much()
    {
        var provider = Create(turnTimeoutMinutes: 60);

        var rotation = provider.Rotate();

        // Long enough that no turn is cut off, short enough that a rotation still means something.
        var window = rotation.RetiredValidUntil!.Value - DateTimeOffset.UtcNow;
        Assert.True(window > TimeSpan.FromMinutes(60), $"window was {window}");
        Assert.True(window < TimeSpan.FromMinutes(70), $"window was {window}");
    }

    [Fact]
    public void A_restart_during_the_grace_window_does_not_cut_the_turn_off()
    {
        var provider = Create();
        var inFlight = provider.Current;
        provider.Rotate();

        // The retired half is on disk for exactly this: a redeploy mid-turn would otherwise undo
        // the whole point of the window.
        Assert.True(Create().Matches(inFlight));
    }

    [Fact]
    public void An_expired_grace_window_stops_being_honoured()
    {
        // A window that ran out while the app was down: the file still names the retired token, and
        // it must not be accepted a moment past what it was given.
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, "mcp-access-token"),
            $"{{\"current\":\"ds_current\",\"retired\":\"ds_retired\",\"retiredUntil\":\"{DateTimeOffset.UtcNow.AddMinutes(-1):O}\"}}");

        var provider = Create();

        Assert.True(provider.Matches("ds_current"));
        Assert.False(provider.Matches("ds_retired"));
        Assert.Null(provider.RetiredValidUntil);
    }

    [Fact]
    public void Rotating_immediately_cuts_the_old_token_off_on_the_spot()
    {
        var provider = Create();
        var leaked = provider.Current;

        var rotation = provider.Rotate(immediately: true);

        Assert.False(provider.Matches(leaked));
        Assert.False(Create().Matches(leaked));
        Assert.Null(rotation.RetiredValidUntil);
        Assert.True(provider.Matches(rotation.Token));
    }

    [Fact]
    public void An_unreadable_token_file_is_replaced_rather_than_fatal()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "mcp-access-token"), "   ");

        var token = Create().Current;

        Assert.StartsWith("ds_", token);
    }

    [Fact]
    public void A_token_written_before_rotation_existed_is_still_read()
    {
        // The file held nothing but the token itself in the first version of this.
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "mcp-access-token"), "ds_from-the-old-format");

        Assert.Equal("ds_from-the-old-format", Create().Current);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }
}
