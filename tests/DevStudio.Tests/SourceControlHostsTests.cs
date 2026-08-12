using DevStudio.Application.Common;
using DevStudio.Domain.Providers;
using DevStudio.Infrastructure.Persistence;
using DevStudio.Infrastructure.SourceControl;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevStudio.Tests;

public class SourceControlHostsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devstudio-hosts-" + Guid.NewGuid().ToString("n"));
    private readonly JsonEntityStore<SourceControlSettings> _store;
    private readonly OrchestratorOptions _options = new();

    public SourceControlHostsTests()
    {
        _options.DataPath = _root;
        _store = new JsonEntityStore<SourceControlSettings>(
            Options.Create(_options),
            NullLogger<JsonEntityStore<SourceControlSettings>>.Instance);
    }

    private SourceControlHosts Create() => new(_store, Options.Create(_options));

    [Fact]
    public void Falls_back_to_the_public_hosts()
    {
        var hosts = Create();

        Assert.Equal("github.com", hosts.Get(SourceControlProvider.GitHub));
        Assert.Equal("gitlab.com", hosts.Get(SourceControlProvider.GitLab));
        Assert.False(hosts.IsOverridden(SourceControlProvider.GitLab));
    }

    [Fact]
    public async Task An_override_is_used_and_survives_a_restart()
    {
        await Create().SetAsync(SourceControlProvider.GitLab, "gitlab.mycompany.com");

        // A fresh instance is what a container restart looks like.
        var restarted = Create();
        await restarted.LoadAsync();

        Assert.Equal("gitlab.mycompany.com", restarted.Get(SourceControlProvider.GitLab));
        Assert.True(restarted.IsOverridden(SourceControlProvider.GitLab));

        // The other forge is untouched.
        Assert.Equal("github.com", restarted.Get(SourceControlProvider.GitHub));
    }

    [Theory]
    [InlineData("https://gitlab.mycompany.com/", "gitlab.mycompany.com")]
    [InlineData("https://gitlab.mycompany.com/group/repo", "gitlab.mycompany.com")]
    [InlineData("  gitlab.mycompany.com  ", "gitlab.mycompany.com")]
    [InlineData("gitlab.mycompany.com/", "gitlab.mycompany.com")]
    public async Task A_pasted_url_is_reduced_to_its_host(string entered, string expected)
    {
        var hosts = Create();

        await hosts.SetAsync(SourceControlProvider.GitLab, entered);

        Assert.Equal(expected, hosts.Get(SourceControlProvider.GitLab));
    }

    [Fact]
    public async Task Clearing_the_host_returns_to_the_default()
    {
        var hosts = Create();
        await hosts.SetAsync(SourceControlProvider.GitLab, "gitlab.mycompany.com");

        await hosts.SetAsync(SourceControlProvider.GitLab, "   ");

        Assert.Equal("gitlab.com", hosts.Get(SourceControlProvider.GitLab));
        Assert.False(hosts.IsOverridden(SourceControlProvider.GitLab));
    }

    [Fact]
    public async Task Configuration_still_supplies_the_default_when_nothing_is_stored()
    {
        _options.GitLabHost = "gitlab.from-config.com";
        var hosts = Create();
        await hosts.LoadAsync();

        Assert.Equal("gitlab.from-config.com", hosts.Get(SourceControlProvider.GitLab));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }
}
