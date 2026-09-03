using DevStudio.Application.Abstractions;
using DevStudio.Application.Remoting;
using DevStudio.Domain.Remoting;

namespace DevStudio.Tests;

/// <summary>
/// Wraps the stub CLI registry, workspace and account services the session tests already build into
/// the execution host the session manager now takes. Every one of those tests exercises local
/// behaviour, so the resolver here answers with the same host whatever it is asked for.
/// </summary>
internal sealed class TestExecutionHost(
    IProviderCliRegistry clis,
    IWorkspaceService workspaces,
    IAccountService accounts) : IExecutionHost, IExecutionHostResolver
{
    public string? RemoteInstanceId => null;
    public string DisplayName => "This machine";

    public IProviderCliRegistry Clis { get; } = clis;
    public IWorkspaceService Workspaces { get; } = workspaces;
    public IAccountService Accounts { get; } = accounts;

    public IWorkspaceFileService Files => throw new NotSupportedException();
    public ITerminalService Terminals => throw new NotSupportedException();

    public Task<RemoteHostConfig> GetConfigAsync(CancellationToken ct = default) =>
        Task.FromResult(RemoteHostConfig.Empty("test"));

    public Task<IReadOnlyList<string>> GetBranchesAsync(string repositoryId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public IExecutionHost Local => this;

    public Task<IExecutionHost> ResolveAsync(string? remoteInstanceId, CancellationToken ct = default) =>
        Task.FromResult<IExecutionHost>(this);

    public Task<IReadOnlyList<RemoteInstance>> AvailableAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RemoteInstance>>([]);
}
