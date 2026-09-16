using DevStudio.Domain.Plugins;

namespace DevStudio.Application.Abstractions;

/// <summary>
/// One plugin this machine already has, as its CLI records it.
/// </summary>
/// <param name="Key">Provider and identifier, which together are what an agent stores.</param>
/// <param name="Origin">
/// Where it was found — the login's home directory, or the config file that lists it. Shown in the
/// UI because a machine with several logins can have a plugin installed for one of them and not the
/// others, and "installed" on its own would then be misleading.
/// </param>
/// <param name="EnabledByDefault">
/// Whether the CLI's own configuration has it switched on outside this app. Informational only: a
/// session gets whatever its agent selected, whichever way this reads.
/// </param>
public sealed record InstalledPlugin(PluginKey Key, string Origin, bool EnabledByDefault)
{
    public string Id => Key.Id;
}

/// <summary>
/// What is installed for the CLIs on this machine. Plugins are installed by the CLIs themselves —
/// <c>claude plugin install</c>, <c>codex plugin install</c>, an npm package in an opencode config —
/// so this reads their own records rather than keeping a second list that could disagree with them.
/// </summary>
public interface IPluginCatalog
{
    /// <summary>Everything installed for any login on this machine, deduplicated by id.</summary>
    Task<IReadOnlyList<InstalledPlugin>> GetAllAsync(CancellationToken ct = default);
}
