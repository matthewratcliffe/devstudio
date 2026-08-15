using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevStudio.Infrastructure.Mcp;

/// <summary>
/// Keeps the orchestrator's own MCP token in a file on the data volume, beside the rest of the
/// state. A file rather than the entity store because it is read on every MCP request and written
/// almost never, and because it should survive a restart without anyone being signed out or asked
/// to paste anything: the same value has to come back after a redeploy or every agent's config is
/// stale at once.
///
/// A rotation retires the old token rather than deleting it. Workspace config is rewritten before
/// every turn, so only a turn already in flight is holding the old value — and it is holding it in
/// a file the running CLI has already read, where nothing can reach in and correct it. Honouring
/// the retired token for as long as a turn is allowed to last means a rotation never interrupts
/// work, while still being over within the hour. An operator who is rotating because the volume
/// leaked wants the opposite, and asks for it explicitly.
/// </summary>
public sealed class McpAccessTokenProvider : IMcpAccessTokenProvider
{
    private const string FileName = "mcp-access-token";

    /// <summary>Longer than a turn may run, so no turn in flight outlives the grace it was given.</summary>
    private static readonly TimeSpan ExtraGrace = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private readonly TimeSpan _grace;
    private readonly ILogger<McpAccessTokenProvider> _logger;
    private readonly Lock _gate = new();
    private State? _state;

    public McpAccessTokenProvider(IOptions<OrchestratorOptions> options, ILogger<McpAccessTokenProvider> logger)
    {
        _path = Path.Combine(options.Value.DataPath, FileName);
        _grace = TimeSpan.FromMinutes(Math.Max(0, options.Value.TurnTimeoutMinutes)) + ExtraGrace;
        _logger = logger;
    }

    public string Current
    {
        get
        {
            lock (_gate)
            {
                return Loaded().Current;
            }
        }
    }

    public DateTimeOffset? RetiredValidUntil
    {
        get
        {
            lock (_gate)
            {
                var state = Loaded();
                return RetiredIsLive(state) ? state.RetiredUntil : null;
            }
        }
    }

    public bool Matches(string? presented)
    {
        if (string.IsNullOrEmpty(presented))
            return false;

        lock (_gate)
        {
            var state = Loaded();

            if (Same(state.Current, presented))
                return true;

            return RetiredIsLive(state) && Same(state.Retired!, presented);
        }
    }

    public McpTokenRotation Rotate(bool immediately = false)
    {
        lock (_gate)
        {
            var replaced = Loaded().Current;

            var state = immediately
                ? new State(Issue(), null, null)
                : new State(Issue(), replaced, DateTimeOffset.UtcNow + _grace);

            Save(state);
            _state = state;

            return new McpTokenRotation(state.Current, RetiredIsLive(state) ? state.RetiredUntil : null);
        }
    }

    /// <summary>The state on disk, reading or creating it the first time it is needed.</summary>
    private State Loaded() => _state ??= Load() ?? Persist(new State(Issue(), null, null));

    /// <summary>True while a retired token is still inside the window it was given.</summary>
    private static bool RetiredIsLive(State state) =>
        state.Retired is { Length: > 0 } && state.RetiredUntil > DateTimeOffset.UtcNow;

    private static bool Same(string expected, string presented)
    {
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(presented);

        // FixedTimeEquals needs equal lengths, and the length of the presented value is not a secret.
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private State? Load()
    {
        try
        {
            if (!File.Exists(_path))
                return null;

            var text = File.ReadAllText(_path).Trim();
            if (text.Length == 0)
                return null;

            // Written as a bare token before rotation had anything to remember; still a valid file.
            if (!text.StartsWith('{'))
                return new State(text, null, null);

            var state = JsonSerializer.Deserialize<State>(text, Json);

            return string.IsNullOrWhiteSpace(state?.Current) ? null : state;
        }
        catch (Exception ex)
        {
            // A token that cannot be read is replaced rather than fatal: a broken file would
            // otherwise take the whole app down on a path nobody can fix from the UI.
            _logger.LogWarning(ex, "Could not read the MCP access token; issuing a new one");
            return null;
        }
    }

    private State Persist(State state)
    {
        Save(state);
        return state;
    }

    private void Save(State state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(state, Json));

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex)
        {
            // Still usable for as long as the process lives; it just will not survive a restart.
            _logger.LogWarning(ex, "Could not persist the MCP access token to {Path}", _path);
        }
    }

    private static string Issue() => "ds_" + Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// The retired half is written down rather than held in memory so a restart during the window
    /// does not do the very thing the window exists to prevent.
    /// </summary>
    private sealed record State(string Current, string? Retired, DateTimeOffset? RetiredUntil);
}
