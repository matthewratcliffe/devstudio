using DevStudio.Desktop;

namespace DevStudio.Tests;

/// <summary>
/// The desktop shells listen on loopback unless somebody says otherwise, and this is the somebody.
/// The environment variable is checked here because it is what a scripted or headless launch uses,
/// and because it has to beat the saved file rather than race it.
/// </summary>
[Collection(nameof(NetworkSettingsTests))]
[CollectionDefinition(nameof(NetworkSettingsTests), DisableParallelization = true)]
public class NetworkSettingsTests : IDisposable
{
    private readonly string? _original = Environment.GetEnvironmentVariable(NetworkSettings.OverrideVariable);

    public void Dispose() =>
        Environment.SetEnvironmentVariable(NetworkSettings.OverrideVariable, _original);

    [Fact]
    public void Loopback_is_what_the_setting_means_when_it_is_off()
    {
        Assert.Equal("127.0.0.1", new NetworkSettings().BindAddress);
        Assert.False(new NetworkSettings().ListenOnLocalNetwork);
    }

    [Fact]
    public void On_means_every_interface()
    {
        Assert.Equal("0.0.0.0", new NetworkSettings { ListenOnLocalNetwork = true }.BindAddress);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("yes")]
    [InlineData("on")]
    public void The_environment_variable_can_turn_it_on_without_writing_a_file(string value)
    {
        Environment.SetEnvironmentVariable(NetworkSettings.OverrideVariable, value);

        Assert.True(NetworkSettings.Load().ListenOnLocalNetwork);
        Assert.True(NetworkSettings.IsForcedByEnvironment);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("no")]
    public void And_can_force_it_off_over_whatever_was_saved(string value)
    {
        Environment.SetEnvironmentVariable(NetworkSettings.OverrideVariable, value);

        Assert.False(NetworkSettings.Load().ListenOnLocalNetwork);

        // Still forced: the shells hide the toggle rather than show a tick that does nothing.
        Assert.True(NetworkSettings.IsForcedByEnvironment);
    }

    [Fact]
    public void An_unset_variable_leaves_the_saved_setting_alone()
    {
        Environment.SetEnvironmentVariable(NetworkSettings.OverrideVariable, null);

        Assert.False(NetworkSettings.IsForcedByEnvironment);
    }

    [Fact]
    public void Addresses_offered_to_other_machines_are_never_loopback()
    {
        // The list is what the shell shows somebody as "use this from your phone", so 127.0.0.1
        // appearing in it would be an instruction that cannot work.
        Assert.DoesNotContain("127.0.0.1", NetworkSettings.LocalAddresses());
    }
}
