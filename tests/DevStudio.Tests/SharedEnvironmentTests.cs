using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Application.Globals;
using DevStudio.Domain.Globals;
using DevStudio.Domain.Providers;
using DevStudio.Infrastructure.Globals;
using DevStudio.Infrastructure.Persistence;
using DevStudio.Infrastructure.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevStudio.Tests;

/// <summary>
/// The install-wide environment: which variables come out for a local launch versus a remote one,
/// and where they sit relative to everything else that sets a variable.
/// </summary>
public class SharedEnvironmentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devstudio-tests-" + Guid.NewGuid().ToString("n"));

    private SharedEnvironment Create(params SharedEnvironmentVariable[] variables)
    {
        var store = new JsonEntityStore<GlobalSettings>(
            Options.Create(new OrchestratorOptions { DataPath = _root }),
            NullLogger<JsonEntityStore<GlobalSettings>>.Instance);

        store.UpsertAsync(new GlobalSettings { SharedEnvironment = [.. variables] }).GetAwaiter().GetResult();

        return new SharedEnvironment(store, NullLogger<SharedEnvironment>.Instance);
    }

    private static SharedEnvironmentVariable Variable(
        string name,
        string value,
        bool enabled = true,
        bool shareWithRemote = false) =>
        new() { Name = name, Value = value, Enabled = enabled, ShareWithRemote = shareWithRemote };

    [Fact]
    public async Task Enabled_variables_are_offered_to_a_local_launch()
    {
        var shared = Create(Variable("FASTMAIL_API_TOKEN", "fm-secret"));

        var resolved = await shared.ForLocalAsync();

        Assert.Equal("fm-secret", resolved["FASTMAIL_API_TOKEN"]);
    }

    [Fact]
    public async Task A_disabled_variable_is_kept_but_never_passed()
    {
        var shared = Create(Variable("FASTMAIL_API_TOKEN", "fm-secret", enabled: false));

        Assert.Empty(await shared.ForLocalAsync());
        Assert.Empty(await shared.ForRemoteAsync());
    }

    [Fact]
    public async Task Only_variables_marked_shareable_leave_this_machine()
    {
        var shared = Create(
            Variable("FASTMAIL_API_TOKEN", "fm-secret"),
            Variable("BUILD_REGION", "eu-west", shareWithRemote: true));

        var local = await shared.ForLocalAsync();
        var remote = await shared.ForRemoteAsync();

        Assert.Equal(2, local.Count);
        Assert.Equal(["BUILD_REGION"], remote.Keys);
    }

    [Fact]
    public async Task A_nameless_row_is_ignored_rather_than_producing_a_blank_variable()
    {
        var shared = Create(Variable("   ", "orphaned"), Variable("KEEP", "yes"));

        Assert.Equal(["KEEP"], (await shared.ForLocalAsync()).Keys);
    }

    [Fact]
    public async Task No_settings_at_all_is_no_variables_rather_than_a_failure()
    {
        var store = new JsonEntityStore<GlobalSettings>(
            Options.Create(new OrchestratorOptions { DataPath = _root }),
            NullLogger<JsonEntityStore<GlobalSettings>>.Instance);

        var shared = new SharedEnvironment(store, NullLogger<SharedEnvironment>.Instance);

        Assert.Empty(await shared.ForLocalAsync());
    }

    [Fact]
    public void The_turn_wins_over_a_shared_variable_of_the_same_name()
    {
        var cli = new CustomCli(
            new CliProvider { Executable = "copilot" },
            new UnusedRunner(),
            new OrchestratorOptions { HomePath = "/home/test" },
            NullLogger.Instance);

        var environment = cli.BuildEnvironment(
            new TurnRequest
            {
                Prompt = "go",
                WorkingDirectory = "/work",
                Environment = new Dictionary<string, string> { ["TOKEN"] = "from-the-agent" },
            },
            new Dictionary<string, string> { ["TOKEN"] = "shared", ["ONLY_SHARED"] = "kept" });

        Assert.Equal("from-the-agent", environment["TOKEN"]);
        Assert.Equal("kept", environment["ONLY_SHARED"]);
    }

    [Fact]
    public void A_shared_variable_cannot_shadow_the_account_home()
    {
        var cli = new CustomCli(
            new CliProvider { Executable = "copilot" },
            new UnusedRunner(),
            new OrchestratorOptions { HomePath = "/home/test" },
            NullLogger.Instance);

        // HOME is what selects the logged-in account, so a stray entry in Settings must not be able
        // to send a turn at somebody else's login.
        var environment = cli.BuildEnvironment(
            new TurnRequest { Prompt = "go", WorkingDirectory = "/work", HomeDirectory = "/home/real" },
            new Dictionary<string, string> { ["HOME"] = "/home/hijacked" });

        Assert.Equal("/home/real", environment["HOME"]);
    }

    [Fact]
    public async Task A_cli_built_without_the_service_simply_has_no_shared_variables()
    {
        var cli = new CustomCli(
            new CliProvider { Executable = "copilot" },
            new UnusedRunner(),
            new OrchestratorOptions { HomePath = "/home/test" },
            NullLogger.Instance);

        Assert.Empty(await cli.SharedAsync(CancellationToken.None));
    }

    /// <summary>These tests build the environment directly and never reach the process layer.</summary>
    private sealed class UnusedRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<int> StreamAsync(
            ProcessRequest request,
            Func<string, bool, CancellationToken, Task> onLine,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }
}
