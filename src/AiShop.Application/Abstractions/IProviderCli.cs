using AiShop.Domain.Agents;
using AiShop.Domain.Providers;

namespace AiShop.Application.Abstractions;

/// <summary>What the orchestrator asks a CLI to do for one turn of a conversation.</summary>
public sealed record TurnRequest
{
    public required string Prompt { get; init; }
    public required string WorkingDirectory { get; init; }
    public PermissionMode PermissionMode { get; init; } = PermissionMode.AcceptEdits;
    public string? Model { get; init; }

    /// <summary>Thinking or reasoning level, as named by the provider.</summary>
    public string? Effort { get; init; }

    /// <summary>
    /// Names of the MCP servers wired into this turn. Their tools have to be allow-listed explicitly:
    /// running headless, there is no one to answer a permission prompt.
    /// </summary>
    public IReadOnlyList<string> McpServerNames { get; init; } = [];

    /// <summary>
    /// Permission rules the operator granted from the UI, in the CLI's allow-list syntax. Same
    /// reason as above: headless, a prompt nobody can answer is a failed tool call.
    /// </summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];
    public string? SystemPrompt { get; init; }

    /// <summary>Provider-side id of an earlier turn; when set the CLI resumes that conversation.</summary>
    public string? ResumeSessionId { get; init; }

    /// <summary>
    /// HOME for the CLI process, which is what selects the logged-in account. Null uses the
    /// container default.
    /// </summary>
    public string? HomeDirectory { get; init; }

    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>();

    public string? ExtraArguments { get; init; }
}

public enum AgentEventKind
{
    /// <summary>Assistant prose to show in the transcript.</summary>
    Text = 0,
    /// <summary>The CLI reported which tool it is about to use.</summary>
    Tool = 1,
    /// <summary>Provider session id captured for resume.</summary>
    SessionId = 2,
    /// <summary>Turn finished; Text carries the final result when the CLI supplies one.</summary>
    Result = 3,
    Error = 4,
    /// <summary>Diagnostics from the CLI that are not part of the conversation.</summary>
    Log = 5,
    /// <summary>
    /// A tool the CLI refused to run without permission. ToolName is the tool; Text describes what
    /// it wanted to do.
    /// </summary>
    PermissionDenied = 6,
}

public sealed record AgentEvent(AgentEventKind Kind, string Text)
{
    public string? ToolName { get; init; }
    public decimal? CostUsd { get; init; }
    public int? DurationMs { get; init; }

    public static AgentEvent Text_(string text) => new(AgentEventKind.Text, text);
    public static AgentEvent Log(string text) => new(AgentEventKind.Log, text);
    public static AgentEvent Error(string text) => new(AgentEventKind.Error, text);
}

/// <summary>
/// Drives one AI CLI. Implementations shell out — there is no API integration anywhere in this app,
/// so the user's existing CLI login is the only credential involved.
/// </summary>
public interface IProviderCli
{
    AiProvider Provider { get; }

    /// <summary>Human-readable CLI name, for the UI.</summary>
    string DisplayName { get; }

    /// <summary>Id of the user-defined CLI this adapter was built from; null for the built-ins.</summary>
    string? DefinitionId => null;

    /// <summary>Runs a single turn, yielding events as the CLI produces them.</summary>
    IAsyncEnumerable<AgentEvent> RunTurnAsync(TurnRequest request, CancellationToken ct);

    /// <summary>
    /// Whether the CLI is installed and logged in. Pass an account's home directory to probe that
    /// account rather than the container default.
    /// </summary>
    Task<ProviderAuthStatus> GetAuthStatusAsync(string? homePath = null, CancellationToken ct = default);

    /// <summary>Login flows this CLI offers, best-supported first.</summary>
    IReadOnlyList<LoginMethod> SupportedLoginMethods { get; }

    /// <summary>Argument list that starts an interactive login in a terminal session.</summary>
    (string FileName, IReadOnlyList<string> Arguments) BuildLoginCommand(LoginMethod method = LoginMethod.Browser);

    /// <summary>Argument list that clears stored credentials.</summary>
    (string FileName, IReadOnlyList<string> Arguments) BuildLogoutCommand();
}

/// <summary>Resolves the CLI adapter for a provider.</summary>
public interface IProviderCliRegistry
{
    /// <summary>Built-in adapters only. Use <see cref="ResolveAsync"/> for user-defined CLIs.</summary>
    IProviderCli Get(AiProvider provider);

    /// <summary>
    /// Resolves an adapter, building one from the stored definition when the provider is Custom.
    /// </summary>
    Task<IProviderCli> ResolveAsync(AiProvider provider, string? cliProviderId, CancellationToken ct = default);

    /// <summary>Built-in adapters.</summary>
    IReadOnlyList<IProviderCli> All { get; }

    /// <summary>Built-in adapters plus one per enabled user-defined CLI.</summary>
    Task<IReadOnlyList<IProviderCli>> GetAllAsync(CancellationToken ct = default);
}
