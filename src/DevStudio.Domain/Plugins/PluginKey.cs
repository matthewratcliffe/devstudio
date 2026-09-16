using DevStudio.Domain.Providers;

namespace DevStudio.Domain.Plugins;

/// <summary>
/// How this app names one CLI extension: the plugin system it belongs to, and the identifier that
/// CLI knows it by — <c>name@marketplace</c> for claude and codex, the package or file name for
/// opencode.
///
/// The provider is part of the name rather than a detail beside it because each CLI has its own
/// plugin ecosystem and the names overlap: claude and codex both have a <c>github</c> plugin, and
/// an agent that switched CLI would otherwise silently take the other one's.
/// </summary>
public readonly record struct PluginKey(AiProvider Provider, string Name)
{
    /// <summary>What an agent stores, e.g. <c>claude:github@claude-plugins-official</c>.</summary>
    public string Id => $"{Prefix(Provider)}:{Name}";

    /// <summary>The marketplace half, for the CLIs that have marketplaces. Null when there is none.</summary>
    public string? Marketplace =>
        Name.LastIndexOf('@') is var at && at > 0 ? Name[(at + 1)..] : null;

    /// <summary>The plugin's own name, without its marketplace.</summary>
    public string ShortName =>
        Name.LastIndexOf('@') is var at && at > 0 ? Name[..at] : Name;

    public override string ToString() => Id;

    /// <summary>
    /// Reads back an id. Null for anything malformed or for a provider this app does not know,
    /// which is what lets a selection made on a newer version be ignored rather than crash.
    /// </summary>
    public static PluginKey? Parse(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var separator = id.IndexOf(':');
        if (separator <= 0 || separator == id.Length - 1)
            return null;

        var provider = id[..separator] switch
        {
            "claude" => AiProvider.Claude,
            "codex" => AiProvider.Codex,
            "opencode" => AiProvider.Opencode,
            _ => (AiProvider?)null,
        };

        return provider is null ? null : new PluginKey(provider.Value, id[(separator + 1)..]);
    }

    /// <summary>Every plugin system this app can configure. Custom CLIs have none.</summary>
    public static IReadOnlyList<AiProvider> SupportedProviders { get; } =
        [AiProvider.Claude, AiProvider.Codex, AiProvider.Opencode];

    public static bool Supports(AiProvider provider) => SupportedProviders.Contains(provider);

    private static string Prefix(AiProvider provider) => provider switch
    {
        AiProvider.Claude => "claude",
        AiProvider.Codex => "codex",
        AiProvider.Opencode => "opencode",
        _ => provider.ToString().ToLowerInvariant(),
    };
}
