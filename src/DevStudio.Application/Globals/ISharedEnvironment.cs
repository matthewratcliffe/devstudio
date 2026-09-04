namespace DevStudio.Application.Globals;

/// <summary>
/// The install-wide environment variables, resolved for one launch.
///
/// Every AI CLI consults this as the lowest layer of its environment, which is what makes a single
/// token reach the CLI, the skills it runs, the hooks it fires and the MCP servers it spawns —
/// rather than only whichever one process it was pasted against.
/// </summary>
public interface ISharedEnvironment
{
    /// <summary>
    /// Every enabled variable, for a CLI running on this machine. Callers layer their own values on
    /// top, so a CLI definition, an agent or the turn always wins over a name set here.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ForLocalAsync(CancellationToken ct = default);

    /// <summary>
    /// Only the enabled variables marked as shareable, for a turn dispatched to another machine.
    /// Kept separate from <see cref="ForLocalAsync"/> because sending a secret over the wire is a
    /// decision somebody has to make per variable, not a side effect of setting one.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ForRemoteAsync(CancellationToken ct = default);
}
