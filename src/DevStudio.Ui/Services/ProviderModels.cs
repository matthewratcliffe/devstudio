using DevStudio.Application.Abstractions;
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
        AiProvider.Opencode => options.OpencodeModels,
        _ => custom.FirstOrDefault(c => c.Id == cliProviderId)?.Models ?? [],
    };

    /// <summary>
    /// Same choices as <see cref="Models"/>, but for a CLI that can say what it actually has right
    /// now — currently only opencode, whose server knows exactly which providers and models it is
    /// configured with — the live list is asked for and merged in ahead of the static suggestions.
    /// Failure to reach the CLI (server not running, no network) just falls back to the static list;
    /// this is a UI convenience, not something a turn should ever be blocked on.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ModelsAsync(
        AiProvider provider,
        string? cliProviderId,
        OrchestratorOptions options,
        IReadOnlyList<CliProvider> custom,
        IProviderCliRegistry registry,
        CancellationToken ct = default)
    {
        var configured = Models(provider, cliProviderId, options, custom);

        IReadOnlyList<string> live;
        try
        {
            var cli = await registry.ResolveAsync(provider, cliProviderId, ct);
            live = await cli.GetAvailableModelsAsync(ct);
        }
        catch
        {
            live = [];
        }

        if (live.Count == 0)
            return configured;

        return live
            .Concat(configured.Where(m => !live.Contains(m, StringComparer.OrdinalIgnoreCase)))
            .ToList();
    }

    public static IReadOnlyList<string> Efforts(
        AiProvider provider,
        string? cliProviderId,
        OrchestratorOptions options,
        IReadOnlyList<CliProvider> custom) => provider switch
    {
        AiProvider.Claude => options.ClaudeEfforts,
        AiProvider.Codex => options.CodexEfforts,
        AiProvider.Opencode => options.OpencodeEfforts,
        _ => custom.FirstOrDefault(c => c.Id == cliProviderId)?.Efforts ?? [],
    };
}
