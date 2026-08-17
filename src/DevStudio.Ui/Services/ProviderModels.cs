using DevStudio.Application.Common;
using DevStudio.Domain.Providers;

namespace DevStudio.Ui.Services;

/// <summary>
/// The model and thinking-level choices offered for a CLI. Built-in providers take theirs from
/// configuration, because model names change faster than this app does; a user-defined CLI carries
/// its own. Every field they fill is still free text at the CLI, so these are suggestions, not a
/// whitelist.
/// </summary>
public static class ProviderModels
{
    public static IReadOnlyList<string> Models(
        AiProvider provider,
        string? cliProviderId,
        OrchestratorOptions options,
        IReadOnlyList<CliProvider> custom) => provider switch
    {
        AiProvider.Claude => options.ClaudeModels,
        AiProvider.Codex => options.CodexModels,
        AiProvider.Opencoder => options.OpencoderModels,
        _ => custom.FirstOrDefault(c => c.Id == cliProviderId)?.Models ?? [],
    };

    public static IReadOnlyList<string> Efforts(
        AiProvider provider,
        string? cliProviderId,
        OrchestratorOptions options,
        IReadOnlyList<CliProvider> custom) => provider switch
    {
        AiProvider.Claude => options.ClaudeEfforts,
        AiProvider.Codex => options.CodexEfforts,
        AiProvider.Opencoder => options.OpencoderEfforts,
        _ => custom.FirstOrDefault(c => c.Id == cliProviderId)?.Efforts ?? [],
    };
}
