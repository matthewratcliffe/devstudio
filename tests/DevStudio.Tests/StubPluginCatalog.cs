using DevStudio.Application.Abstractions;
using DevStudio.Domain.Plugins;
using DevStudio.Domain.Providers;

namespace DevStudio.Tests;

/// <summary>
/// A machine with the given plugins installed — empty unless a test says otherwise, which is what
/// every <see cref="WorkspaceService"/> under test needs wired up.
/// </summary>
internal sealed class StubPluginCatalog(params InstalledPlugin[] plugins) : IPluginCatalog
{
    public Task<IReadOnlyList<InstalledPlugin>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<InstalledPlugin>>(plugins);

    /// <summary>One installed plugin, named the way its CLI names it.</summary>
    public static InstalledPlugin Installed(AiProvider provider, string name, bool enabled = false) =>
        new(new PluginKey(provider, name), "home", enabled);
}
