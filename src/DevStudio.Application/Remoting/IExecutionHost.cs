using DevStudio.Application.Abstractions;
using DevStudio.Domain.Remoting;

namespace DevStudio.Application.Remoting;

/// <summary>
/// The machine a session's work actually happens on. Everything here is filesystem- or
/// process-bound — where the checkout is, which login the CLI runs as, what is installed — which is
/// precisely the set of things that stops being true when the work moves to another machine.
///
/// Everything that is *not* here — the session, its transcript, the agent, the project, the queue
/// that dispatched it — stays local whichever host is chosen. An operator has one place they look
/// for their conversations, and it does not move about depending on where a turn ran.
/// </summary>
public interface IExecutionHost
{
    /// <summary>Null for this machine; otherwise the id of the <see cref="RemoteInstance"/>.</summary>
    string? RemoteInstanceId { get; }

    /// <summary>For the UI, and for the line in a transcript saying where a turn ran.</summary>
    string DisplayName { get; }

    bool IsLocal => RemoteInstanceId is null;

    IProviderCliRegistry Clis { get; }
    IWorkspaceService Workspaces { get; }
    IAccountService Accounts { get; }
    IWorkspaceFileService Files { get; }
    ITerminalService Terminals { get; }

    /// <summary>
    /// What this host offers, for the pickers: its CLIs and their models, its MCP servers, skills,
    /// checkouts and logins. A remote answers from its own configuration, so choosing an instance
    /// changes every dependent dropdown on the page.
    /// </summary>
    Task<RemoteHostConfig> GetConfigAsync(CancellationToken ct = default);

    /// <summary>Branches in one of this host's repositories, for the base-branch picker.</summary>
    Task<IReadOnlyList<string>> GetBranchesAsync(string repositoryId, CancellationToken ct = default);
}

/// <summary>
/// Hands out the execution host for an id. Null — the overwhelmingly common case — is this machine.
/// </summary>
public interface IExecutionHostResolver
{
    /// <summary>This machine.</summary>
    IExecutionHost Local { get; }

    /// <summary>
    /// The host for an id. Throws when the instance is unknown, disabled, or has never been paired,
    /// because silently running somewhere other than where the operator chose is worse than failing.
    /// </summary>
    Task<IExecutionHost> ResolveAsync(string? remoteInstanceId, CancellationToken ct = default);

    /// <summary>
    /// Every host that could be picked right now: this machine first, then each paired and enabled
    /// remote. Reachability is not checked here — a remote that is merely asleep should still be
    /// offered, and the connection error when it is used says more than its absence would.
    /// </summary>
    Task<IReadOnlyList<RemoteInstance>> AvailableAsync(CancellationToken ct = default);
}
