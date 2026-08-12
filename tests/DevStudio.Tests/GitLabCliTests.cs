using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Domain.Providers;
using DevStudio.Infrastructure.SourceControl;
using Microsoft.Extensions.Options;

namespace DevStudio.Tests;

public class GitLabCliTests
{
    private static (GitLabCli Cli, RecordingRunner Runner) Create(string stdout = "[]", int exitCode = 0)
    {
        var runner = new RecordingRunner(new ProcessResult(exitCode, stdout, string.Empty, false));
        var options = Options.Create(new OrchestratorOptions { HomePath = "/home/test", GitLabCliExecutable = "glab" });
        return (new GitLabCli(runner, new FixedHosts("gitlab.com"), options), runner);
    }

    [Fact]
    public async Task Listing_repositories_asks_for_everything_the_account_is_a_member_of()
    {
        var (cli, runner) = Create();

        await cli.ListRepositoriesAsync(100);

        // Without --member glab returns owned projects only, which leaves out every group project.
        Assert.Equal(["repo", "list", "--member", "--per-page", "100", "--output", "json"], runner.LastRequest!.Arguments);
    }

    [Fact]
    public async Task Group_projects_come_back_with_their_full_path()
    {
        const string json = """
        [
          { "path_with_namespace": "acme/platform/api", "description": "The API", "visibility": "private",
            "http_url_to_repo": "https://gitlab.com/acme/platform/api.git" },
          { "path_with_namespace": "matt/scratch", "description": null, "visibility": "public",
            "http_url_to_repo": "https://gitlab.com/matt/scratch.git" }
        ]
        """;

        var (cli, _) = Create(json);

        var repos = await cli.ListRepositoriesAsync();

        Assert.Equal(["acme/platform/api", "matt/scratch"], repos.Select(r => r.FullName));
        Assert.True(repos[0].IsPrivate);
        Assert.False(repos[1].IsPrivate);
        Assert.Equal("https://gitlab.com/acme/platform/api.git", repos[0].CloneUrl);
    }

    [Fact]
    public async Task A_failing_command_lists_nothing_rather_than_throwing()
    {
        var (cli, _) = Create("not json", exitCode: 1);

        Assert.Empty(await cli.ListRepositoriesAsync());
    }

    [Fact]
    public async Task Git_is_pointed_at_glab_for_credentials_on_the_configured_host()
    {
        var runner = new RecordingRunner(new ProcessResult(0, string.Empty, string.Empty, false));
        var options = Options.Create(new OrchestratorOptions
        {
            HomePath = "/home/test",
            GitLabCliExecutable = "glab",
            GitExecutable = "git",
        });
        var cli = new GitLabCli(runner, new FixedHosts("gitlab.htrak.com"), options);

        var result = await cli.ConfigureGitCredentialsAsync();

        Assert.True(result.Succeeded);
        Assert.Equal("git", runner.LastRequest!.FileName);
        Assert.Equal(
            [
                "config", "--global", "--replace-all",
                "credential.https://gitlab.htrak.com.helper",
                "!glab auth git-credential",
            ],
            runner.LastRequest.Arguments);
    }

    [Fact]
    public void A_token_login_reads_the_token_from_stdin_rather_than_the_command_line()
    {
        var (cli, _) = Create();

        var (fileName, arguments) = cli.BuildLoginCommand(LoginMethod.Token);

        Assert.Equal("glab", fileName);
        Assert.Contains("--stdin", arguments);
        Assert.DoesNotContain("--token", arguments);
    }

    private sealed class RecordingRunner(ProcessResult result) : IProcessRunner
    {
        public ProcessRequest? LastRequest { get; private set; }

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(result);
        }

        public Task<int> StreamAsync(ProcessRequest request, Func<string, bool, CancellationToken, Task> onLine, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FixedHosts(string host) : ISourceControlHosts
    {
        public string Get(SourceControlProvider provider) => host;
        public bool IsOverridden(SourceControlProvider provider) => false;
        public Task SetAsync(SourceControlProvider provider, string? host, CancellationToken ct = default) => Task.CompletedTask;
        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
