using DevStudio.Domain.Common;

namespace DevStudio.Domain.Providers;

/// <summary>How the orchestrator reads what a CLI prints.</summary>
public enum CliOutputFormat
{
    /// <summary>Everything the CLI writes is the answer. Simple, and works with almost anything.</summary>
    PlainText = 0,

    /// <summary>One JSON object per line; the interesting fields are named in the definition.</summary>
    JsonLines = 1,
}

/// <summary>How the orchestrator talks to a user-defined provider.</summary>
public enum CliTransport
{
    /// <summary>Run a command per turn and read what it prints. The original shape.</summary>
    Process = 0,

    /// <summary>
    /// Agent Client Protocol: spawn the agent and talk JSON-RPC over its stdio. The agent does the
    /// work and reports back, so tools, file edits and permission prompts all come from it.
    /// </summary>
    Acp = 1,

    /// <summary>
    /// An OpenAI-compatible HTTP endpoint — llama.cpp's server, ollama, LM Studio. A bare model has
    /// no tools of its own, so the orchestrator runs the tool loop and executes them itself.
    /// </summary>
    OpenAiCompatible = 2,
}

/// <summary>
/// A user-defined agent backend, so anything already installed or running locally can drive agents
/// without a code change. Claude and Codex are built in because their output shapes are fiddly;
/// everything else goes through here. <see cref="Transport"/> decides which of the fields below
/// apply — process settings, an ACP command, or an HTTP endpoint.
/// </summary>
public sealed class CliProvider : Entity
{
    public string Name { get; set; } = "New CLI";
    public string Description { get; set; } = string.Empty;

    public CliTransport Transport { get; set; } = CliTransport.Process;

    /// <summary>Executable name or absolute path, e.g. <c>copilot</c>. Process and ACP transports.</summary>
    public string Executable { get; set; } = string.Empty;

    /// <summary>
    /// Arguments that start the agent in ACP mode, e.g. <c>--experimental-acp</c>. No token
    /// substitution: the same command serves every turn, and the conversation happens over stdio.
    /// </summary>
    public string AcpArguments { get; set; } = string.Empty;

    /// <summary>
    /// Root of an OpenAI-compatible API, e.g. <c>http://localhost:8080/v1</c> for llama-server.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Sent as a bearer token when set. Local servers usually need nothing; this is here for the
    /// ones started with an API key.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// How many times the model may call tools before the turn is cut short. A model that loops
    /// would otherwise run until the turn times out.
    /// </summary>
    public int MaxToolCalls { get; set; } = 25;

    /// <summary>
    /// Stream the answer as it is written. Worth turning off for a server that only reports tool
    /// calls on a whole response — the reply then arrives in one piece, but the tools work.
    /// </summary>
    public bool Stream { get; set; } = true;

    /// <summary>
    /// Arguments for one turn. Tokens are substituted per argument, so <c>{{prompt}}</c> stays a
    /// single argument however long it is. Available: prompt, systemPrompt, model, workdir.
    /// An argument whose token resolves to nothing is dropped, which is how optionals work.
    /// </summary>
    public string PromptArguments { get; set; } = "-p {{prompt}}";

    /// <summary>
    /// Arguments appended when continuing an existing conversation, with <c>{{sessionId}}</c>.
    /// Empty means the CLI cannot resume and every turn starts fresh.
    /// </summary>
    public string ResumeArguments { get; set; } = string.Empty;

    /// <summary>Appended for a model override; uses <c>{{model}}</c>.</summary>
    public string ModelArguments { get; set; } = string.Empty;

    /// <summary>Models offered in the dropdown for this CLI.</summary>
    public List<string> Models { get; set; } = [];

    /// <summary>Appended for a thinking level; uses <c>{{effort}}</c>.</summary>
    public string EffortArguments { get; set; } = string.Empty;

    /// <summary>Thinking levels offered in the dropdown for this CLI.</summary>
    public List<string> Efforts { get; set; } = [];

    /// <summary>Extra arguments per permission mode, keyed by mode name.</summary>
    public Dictionary<string, string> PermissionArguments { get; set; } = [];

    public CliOutputFormat OutputFormat { get; set; } = CliOutputFormat.PlainText;

    /// <summary>JSON property holding assistant text. Dotted paths are supported.</summary>
    public string TextProperty { get; set; } = "text";

    /// <summary>JSON property holding the conversation id to resume from.</summary>
    public string SessionIdProperty { get; set; } = "session_id";

    /// <summary>JSON property holding an error message.</summary>
    public string ErrorProperty { get; set; } = "error";

    /// <summary>Command that signs in, run in the browser terminal. Just the arguments.</summary>
    public string LoginArguments { get; set; } = string.Empty;

    public string LogoutArguments { get; set; } = string.Empty;

    /// <summary>Command that reports whether the CLI is signed in.</summary>
    public string StatusArguments { get; set; } = string.Empty;

    /// <summary>
    /// Text in the status output that means signed out. Case-insensitive. Empty falls back to the
    /// status command's exit code.
    /// </summary>
    public string LoggedOutMarker { get; set; } = "not logged in";

    /// <summary>Environment variables set for every invocation.</summary>
    public Dictionary<string, string> Environment { get; set; } = [];

    public bool Enabled { get; set; } = true;

    /// <summary>Accent used for this provider's pills in the UI.</summary>
    public string Accent { get; set; } = "lime";
}
