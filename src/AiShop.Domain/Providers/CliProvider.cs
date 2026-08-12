using AiShop.Domain.Common;

namespace AiShop.Domain.Providers;

/// <summary>How the orchestrator reads what a CLI prints.</summary>
public enum CliOutputFormat
{
    /// <summary>Everything the CLI writes is the answer. Simple, and works with almost anything.</summary>
    PlainText = 0,

    /// <summary>One JSON object per line; the interesting fields are named in the definition.</summary>
    JsonLines = 1,
}

/// <summary>
/// A user-defined AI CLI, so any locally installed and already-signed-in tool can drive agents
/// without a code change. Claude and Codex are built in because their output shapes are fiddly;
/// everything else goes through here.
/// </summary>
public sealed class CliProvider : Entity
{
    public string Name { get; set; } = "New CLI";
    public string Description { get; set; } = string.Empty;

    /// <summary>Executable name or absolute path, e.g. <c>copilot</c>.</summary>
    public string Executable { get; set; } = string.Empty;

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
