using DevStudio.Domain.Providers;

namespace DevStudio.Application.Remoting;

/// <summary>
/// Names of the hub methods, shared so the two sides cannot drift apart silently. The client half
/// of this feature and the server half live in the same assembly and are always deployed together,
/// but the two ends are different processes on different machines and may well be different
/// versions, so the contract is written down rather than assumed.
/// </summary>
public static class RemoteHubMethods
{
    public const string Path = "/hubs/remote";

    public const string GetConfig = nameof(GetConfig);
    public const string RunTurn = nameof(RunTurn);
    public const string GetAuthStatus = nameof(GetAuthStatus);

    public const string PrepareWorkspace = nameof(PrepareWorkspace);
    public const string ReleaseWorkspace = nameof(ReleaseWorkspace);
    public const string MaterialiseSkills = nameof(MaterialiseSkills);
    public const string MaterialiseMcp = nameof(MaterialiseMcp);
    public const string MaterialiseProjectFiles = nameof(MaterialiseProjectFiles);
    public const string MaterialiseGlobalFiles = nameof(MaterialiseGlobalFiles);
    public const string WriteGuidance = nameof(WriteGuidance);
    public const string ComposeSystemPrompt = nameof(ComposeSystemPrompt);

    public const string ResolveAccount = nameof(ResolveAccount);
    public const string GetBranches = nameof(GetBranches);

    public const string ListWorkspaceFiles = nameof(ListWorkspaceFiles);
    public const string ReadWorkspaceFile = nameof(ReadWorkspaceFile);

    public const string StartTerminal = nameof(StartTerminal);
    public const string StreamTerminal = nameof(StreamTerminal);
    public const string SendTerminal = nameof(SendTerminal);
    public const string SendTerminalSecret = nameof(SendTerminalSecret);
    public const string SendTerminalControl = nameof(SendTerminalControl);
    public const string StopTerminal = nameof(StopTerminal);
}

/// <summary>Paths the pairing handshake uses, before there is any key to connect the hub with.</summary>
public static class RemotePairingRoutes
{
    public const string Request = "/remote/pair/request";

    /// <summary>Polled by the requester until somebody at the other machine decides.</summary>
    public const string Status = "/remote/pair/status/{requestId}";

    /// <summary>Called with a key in hand, to check it is still good and learn who granted it.</summary>
    public const string Hello = "/remote/hello";
}

/// <summary>What one instance says about itself when asking another for access.</summary>
public sealed record RemotePairingRequest(string InstanceId, string InstanceName, string MachineName, string? Version);

/// <summary>
/// The answer to a pairing request. <paramref name="Token"/> is set only once approved; until then
/// the requester holds the code and shows it, so the two screens can be compared.
/// </summary>
public sealed record RemotePairingResponse(
    string RequestId,
    string VerificationCode,
    string Status,
    string? Token = null,
    DateTimeOffset? ExpiresAt = null,
    string? HostName = null,
    string? HostVersion = null,
    string? Detail = null);

/// <summary>Who a key belongs to, answered by the host to a caller already holding one.</summary>
public sealed record RemoteHelloResponse(string HostName, string? HostVersion, string GrantId, DateTimeOffset? ExpiresAt);

/// <summary>
/// One CLI the far side can run, with the model and effort lists it would offer for that CLI. The
/// lists are resolved over there rather than here: they come from the remote's own configuration
/// and, for the CLIs that can be asked, from the remote's own running server.
/// </summary>
public sealed record RemoteCliDescriptor(
    AiProvider Provider,
    string? CliProviderId,
    string DisplayName,
    IReadOnlyList<string> Models,
    IReadOnlyList<string> Efforts)
{
    /// <summary>
    /// Same keying the local pickers use — built-ins by provider, user-defined CLIs by definition
    /// id — so a selection means the same thing whichever side answered.
    /// </summary>
    public string Key => CliProviderId is { Length: > 0 } id ? $"custom:{id}" : Provider.ToString();
}

public sealed record RemoteNamedItem(string Id, string Name, bool IsDefault = false, string? Detail = null);

/// <summary>
/// Everything the far side's dropdowns need, fetched in one call. One call rather than several
/// because these are all read together whenever a picker changes, and a page that lit up field by
/// field over a slow link would be worse than one that waited once.
/// </summary>
public sealed record RemoteHostConfig(
    string HostName,
    string? HostVersion,
    IReadOnlyList<RemoteCliDescriptor> Clis,
    IReadOnlyList<RemoteNamedItem> McpServers,
    IReadOnlyList<RemoteNamedItem> Skills,
    IReadOnlyList<RemoteNamedItem> Repositories,
    IReadOnlyList<RemoteNamedItem> Accounts,
    bool IsWindows = false)
{
    /// <summary>
    /// The shell a typed command line should be handed to over there. Decided from what the far side
    /// reported rather than from this machine, which is the whole point — a Windows desktop driving a
    /// Linux container would otherwise send it <c>cmd /c</c>.
    /// </summary>
    public (string FileName, IReadOnlyList<string> Arguments) ShellFor(string commandLine) =>
        IsWindows
            ? ("cmd.exe", ["/c", commandLine])
            : ("/bin/sh", ["-lc", commandLine]);

    public static RemoteHostConfig Empty(string hostName) =>
        new(hostName, null, [], [], [], [], []);
}

/// <summary>Arguments for preparing a workspace on the far side. Mirrors IWorkspaceService.PrepareAsync.</summary>
public sealed record RemoteWorkspaceRequest(
    string AgentJson,
    string SessionId,
    string? ProjectId,
    IReadOnlyList<string>? ExtraServerIds);

/// <summary>
/// A workspace as it exists on the far side. The path is meaningless here, which is exactly the
/// point — it is handed straight back to the remote in the turn request and never touched locally.
/// </summary>
public sealed record RemoteWorkspace(string Path, string? RepositoryId, string? WorktreeId, string? WorktreeJson, string? ProjectId);

public sealed record RemoteAccountResult(string? AccountId, string Name, string HomePath, RemoteAccountResult? Fallback = null);

public sealed record RemoteWorkspaceFile(
    string RelativePath,
    string Name,
    long SizeBytes,
    DateTimeOffset ModifiedAt,
    bool IsImage,
    bool IsText);

public sealed record RemoteFileContent(string FileName, string ContentType, byte[] Content);

/// <summary>What a terminal on the far side looks like at one moment.</summary>
public sealed record RemoteTerminalState(
    string Id,
    bool IsRunning,
    int? ExitCode,
    string Buffer,
    IReadOnlyList<string> DetectedUrls,
    IReadOnlyList<string> DetectedCodes);

public sealed record RemoteTerminalStart(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment,
    bool PreferPseudoTerminal);
