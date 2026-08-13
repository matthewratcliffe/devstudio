using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Application.Teams;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Mcp;
using DevStudio.Domain.Projects;
using DevStudio.Domain.Repositories;
using DevStudio.Domain.Scheduling;
using DevStudio.Domain.Sessions;
using DevStudio.Domain.Skills;
using DevStudio.Domain.Workflows;
using DevStudio.Infrastructure.Git;
using DevStudio.Infrastructure.Updates;
using DevStudio.Infrastructure.Images;
using DevStudio.Infrastructure.Mcp;
using DevStudio.Infrastructure.SourceControl;
using DevStudio.Infrastructure.Persistence;
using DevStudio.Infrastructure.Processes;
using DevStudio.Infrastructure.Providers;
using DevStudio.Infrastructure.Providers.Acp;
using DevStudio.Infrastructure.Providers.OpenAi;
using DevStudio.Infrastructure.Scheduling;
using DevStudio.Infrastructure.Skills;
using DevStudio.Infrastructure.Seed;
using DevStudio.Infrastructure.Teams;
using DevStudio.Infrastructure.Terminals;
using DevStudio.Infrastructure.Workspaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DevStudio.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<OrchestratorOptions>(configuration.GetSection(OrchestratorOptions.SectionName));

        // Relative paths are convenient in appsettings but every consumer wants an absolute one,
        // and an empty HomePath means "use the real user home" — which is what a dev machine needs.
        services.PostConfigure<OrchestratorOptions>(options =>
        {
            options.HomePath = string.IsNullOrWhiteSpace(options.HomePath)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : Path.GetFullPath(options.HomePath);

            options.DataPath = Path.GetFullPath(options.DataPath);
            options.RepositoriesPath = Path.GetFullPath(options.RepositoriesPath);
            options.WorktreesPath = Path.GetFullPath(options.WorktreesPath);
            options.ScratchPath = Path.GetFullPath(options.ScratchPath);
        });

        // Persistence — one JSON collection per entity type on the mounted volume.
        services.AddSingleton(typeof(IEntityStore<>), typeof(JsonEntityStore<>));

        services.AddSingleton<IProcessRunner, ProcessRunner>();

        // Provider CLIs. Adding another AI CLI means adding an IProviderCli here.
        services.AddSingleton<IProviderCli, ClaudeCli>();
        services.AddSingleton<IProviderCli, CodexCli>();
        services.AddSingleton<IProviderCliRegistry, ProviderCliRegistry>();

        // User-defined providers that are not plain commands: an ACP agent driven over its stdio,
        // and OpenAI-compatible endpoints, whose conversations the orchestrator has to remember.
        services.AddSingleton<IAcpConnectionFactory, AcpProcessConnectionFactory>();
        services.AddSingleton<ConversationStore>();
        services.AddSingleton<IAccountService, AccountService>();

        // Used to replay OAuth callbacks to the CLI's own loopback listener.
        services.AddHttpClient();
        services.AddSingleton<ILoopbackCallbackForwarder, LoopbackCallbackForwarder>();
        services.AddSingleton<IMcpTokenService, McpTokenService>();
        services.AddSingleton<IMcpProbeService, McpProbeService>();

        // Image backends. All three are registered whether or not they hold credentials — the UI
        // shows what each one needs, which is more use than a backend that silently is not there.
        // Their keys live on the volume with the CLI accounts, not in configuration.
        services.AddSingleton<IImageSettingsService, ImageSettingsService>();
        services.AddSingleton<IImageGenerator, PollinationsImageGenerator>();
        services.AddSingleton<IImageGenerator, CloudflareImageGenerator>();
        services.AddSingleton<IImageGenerator, GeminiImageGenerator>();
        services.AddSingleton<IImageGenerationService, ImageGenerationService>();

        // Named client so the GitHub API sees a User-Agent, which it requires and rejects requests
        // without.
        services.AddHttpClient<IReleaseChecker, GitHubReleaseChecker>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("devStudio");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        });

        // The public skill registry the `npx skills` CLI installs from. Talking to its API directly
        // keeps skill pulls off a Node runtime, and the import lands in the library rather than in
        // whichever workspace happened to be open.
        services.AddHttpClient<ISkillRegistry, SkillsShRegistry>(client =>
        {
            client.BaseAddress = new Uri("https://skills.sh/");
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("devStudio");
        });
        services.AddSingleton<ISkillImporter, SkillImporter>();

        services.AddSingleton<IGitService, GitService>();
        services.AddSingleton<ISourceControlHosts, SourceControlHosts>();
        services.AddSingleton<ISourceControlCli, GitHubCli>();
        services.AddSingleton<ISourceControlCli, GitLabCli>();
        services.AddSingleton<ISourceControlRegistry, SourceControlRegistry>();
        services.AddSingleton<ITerminalService, TerminalService>();
        services.AddSingleton<IWorkspaceService, WorkspaceService>();
        services.AddSingleton<IFileLibraryService, FileLibraryService>();
        services.AddSingleton<IWorkspaceFileService, WorkspaceFileService>();

        // Shared definitions read out of a git repository, and the catch-up import on start.
        services.AddSingleton<ITeamSettingsService, TeamSettingsService>();
        services.AddHostedService<TeamSyncHostedService>();

        services.AddSingleton<SchedulerHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<SchedulerHostedService>());
        services.AddHostedService<SeedHostedService>();

        return services;
    }
}
