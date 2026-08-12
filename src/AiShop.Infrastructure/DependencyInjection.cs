using AiShop.Application.Abstractions;
using AiShop.Application.Common;
using AiShop.Domain.Agents;
using AiShop.Domain.Mcp;
using AiShop.Domain.Projects;
using AiShop.Domain.Repositories;
using AiShop.Domain.Scheduling;
using AiShop.Domain.Sessions;
using AiShop.Domain.Skills;
using AiShop.Domain.Workflows;
using AiShop.Infrastructure.Git;
using AiShop.Infrastructure.Mcp;
using AiShop.Infrastructure.SourceControl;
using AiShop.Infrastructure.Persistence;
using AiShop.Infrastructure.Processes;
using AiShop.Infrastructure.Providers;
using AiShop.Infrastructure.Scheduling;
using AiShop.Infrastructure.Seed;
using AiShop.Infrastructure.Terminals;
using AiShop.Infrastructure.Workspaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AiShop.Infrastructure;

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
        services.AddSingleton<IAccountService, AccountService>();

        // Used to replay OAuth callbacks to the CLI's own loopback listener.
        services.AddHttpClient();
        services.AddSingleton<ILoopbackCallbackForwarder, LoopbackCallbackForwarder>();
        services.AddSingleton<IMcpTokenService, McpTokenService>();
        services.AddSingleton<IMcpProbeService, McpProbeService>();

        services.AddSingleton<IGitService, GitService>();
        services.AddSingleton<ISourceControlHosts, SourceControlHosts>();
        services.AddSingleton<ISourceControlCli, GitHubCli>();
        services.AddSingleton<ISourceControlCli, GitLabCli>();
        services.AddSingleton<ISourceControlRegistry, SourceControlRegistry>();
        services.AddSingleton<ITerminalService, TerminalService>();
        services.AddSingleton<IWorkspaceService, WorkspaceService>();
        services.AddSingleton<IFileLibraryService, FileLibraryService>();
        services.AddSingleton<IWorkspaceFileService, WorkspaceFileService>();

        services.AddSingleton<SchedulerHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<SchedulerHostedService>());
        services.AddHostedService<SeedHostedService>();

        return services;
    }
}
