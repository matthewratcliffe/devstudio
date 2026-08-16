using DevStudio.Application.Abstractions;
using DevStudio.Domain.Globals;
using Microsoft.Extensions.Hosting;

namespace DevStudio.Infrastructure.Workspaces;

/// <summary>Live workspace path policy shared by providers and the browser file service.</summary>
public sealed class WorkspacePathPolicy
{
    public bool ValidatePaths { get; private set; } = true;
    public bool FollowSymlinks { get; private set; }

    public void Apply(GlobalSettings settings)
    {
        ValidatePaths = settings.ValidateWorkspacePaths;
        FollowSymlinks = settings.FollowWorkspaceSymlinks;
    }
}

/// <summary>Loads the persisted workspace path policy before the application accepts work.</summary>
public sealed class WorkspacePathPolicyLoader : IHostedService
{
    private readonly IEntityStore<GlobalSettings> _settings;
    private readonly WorkspacePathPolicy _policy;

    public WorkspacePathPolicyLoader(IEntityStore<GlobalSettings> settings, WorkspacePathPolicy policy)
    {
        _settings = settings;
        _policy = policy;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (await _settings.GetAsync(GlobalSettings.WellKnownId, cancellationToken) is { } settings)
            _policy.Apply(settings);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
