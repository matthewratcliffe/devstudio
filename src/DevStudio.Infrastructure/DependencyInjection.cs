using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Application.Globals;
using DevStudio.Application.Remoting;
using DevStudio.Application.Sessions;
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
using DevStudio.Infrastructure.Globals;
using DevStudio.Infrastructure.Updates;
using DevStudio.Infrastructure.Images;
using DevStudio.Infrastructure.Mcp;
using DevStudio.Infrastructure.SourceControl;
using DevStudio.Infrastructure.Persistence;
using DevStudio.Infrastructure.Processes;
using DevStudio.Infrastructure.Providers;
using DevStudio.Infrastructure.Providers.Acp;
using DevStudio.Infrastructure.Providers.OpenAi;
using DevStudio.Infrastructure.Queues;
using DevStudio.Infrastructure.Remoting;
using DevStudio.Infrastructure.Scheduling;
using DevStudio.Infrastructure.Skills;
using DevStudio.Infrastructure.Seed;
using DevStudio.Infrastructure.Sessions;
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
        services.AddSingleton<WorkspacePathPolicy>();
        services.AddHostedService<WorkspacePathPolicyLoader>();

        services.AddSingleton<IProcessRunner, ProcessRunner>();

        // Install-wide environment variables, consulted by every CLI adapter below.
        services.AddSingleton<ISharedEnvironment, SharedEnvironment>();

        // Provider CLIs. Adding another AI CLI means adding an IProviderCli here.
        services.AddSingleton<IProviderCli, ClaudeCli>();
        services.AddSingleton<IProviderCli, CodexCli>();
        services.AddSingleton<IProviderCli, OpencodeCli>();
        services.AddSingleton<IProviderCliRegistry, ProviderCliRegistry>();
        services.AddSingleton<IOpencodeServerManager, OpencodeServerManager>();

        // User-defined providers that are not plain commands: an ACP agent driven over its stdio,
        // and OpenAI-compatible endpoints, whose conversations the orchestrator has to remember.
        services.AddSingleton<IAcpConnectionFactory, AcpProcessConnectionFactory>();
        services.AddSingleton<ConversationStore>();
        services.AddSingleton<IAccountService, AccountService>();

        // Used to replay OAuth callbacks to the CLI's own loopback listener.
        services.AddHttpClient();
        services.AddSingleton<ILoopbackCallbackForwarder, LoopbackCallbackForwarder>();
        // The secret guarding this app's own MCP endpoints. Registered before the token service,
        // which hands it to agents as the credential for the built-in servers.
        services.AddSingleton<IMcpAccessTokenProvider, McpAccessTokenProvider>();
        services.AddSingleton<IMcpTokenService, McpTokenService>();
        services.AddSingleton<IMcpProbeService, McpProbeService>();
        services.AddSingleton<IMcpAuthDiscovery, McpAuthDiscoveryService>();
        services.AddSingleton<IMcpOAuthService, McpOAuthService>();

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

        // Standards reference files read out of their own git repository: pulled on an interval and
        // best-effort before every new session, so they never go stale without anybody noticing.
        services.AddSingleton<IStandardsFilesSyncService, StandardsFilesSyncService>();
        services.AddHostedService<StandardsFilesSyncHostedService>();

        services.AddSingleton<SchedulerHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<SchedulerHostedService>());

        // Resolvable on its own as well, so the UI can poke it the moment an item is added rather
        // than leaving the operator watching a queue that looks stuck until the next tick.
        services.AddSingleton<QueueDispatcherHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<QueueDispatcherHostedService>());
        services.AddHostedService<SeedHostedService>();

        // Keeps the sessions list to current work. Resolvable on its own so the UI can sweep on
        // demand as well.
        // Remoting. The local host is registered as the implementation of IExecutionHost so that
        // everything which does not care where it runs keeps resolving exactly what it always did;
        // the resolver is the only thing that ever hands back anything else.
        services.AddSingleton<IExecutionHost, LocalExecutionHost>();
        services.AddSingleton<IRemoteConnectionPool, RemoteConnectionPool>();
        services.AddSingleton<IExecutionHostResolver, ExecutionHostResolver>();
        services.AddSingleton<IRemoteTokenIssuer, RemoteTokenIssuer>();
        services.AddSingleton<IRemoteAccessService, RemoteAccessService>();
        services.AddSingleton<IRemoteInstancePairing, RemoteInstancePairing>();

        services.AddSingleton<ISessionArchiver, SessionArchiver>();
        services.AddSingleton<ISessionIdleCloser, SessionIdleCloser>();
        services.AddHostedService<SessionSweepHostedService>();

        return services;
    }
}
