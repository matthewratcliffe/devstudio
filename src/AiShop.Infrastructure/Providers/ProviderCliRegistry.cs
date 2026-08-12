using AiShop.Application.Abstractions;
using AiShop.Application.Common;
using AiShop.Domain.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiShop.Infrastructure.Providers;

public sealed class ProviderCliRegistry : IProviderCliRegistry
{
    private readonly Dictionary<AiProvider, IProviderCli> _byProvider;
    private readonly IEntityStore<CliProvider> _definitions;
    private readonly IProcessRunner _runner;
    private readonly OrchestratorOptions _options;
    private readonly ILoggerFactory _loggerFactory;

    public ProviderCliRegistry(
        IEnumerable<IProviderCli> clis,
        IEntityStore<CliProvider> definitions,
        IProcessRunner runner,
        IOptions<OrchestratorOptions> options,
        ILoggerFactory loggerFactory)
    {
        _byProvider = clis.ToDictionary(c => c.Provider);
        _definitions = definitions;
        _runner = runner;
        _options = options.Value;
        _loggerFactory = loggerFactory;

        All = _byProvider.Values.OrderBy(c => c.Provider).ToList();
    }

    public IReadOnlyList<IProviderCli> All { get; }

    public IProviderCli Get(AiProvider provider) =>
        _byProvider.TryGetValue(provider, out var cli)
            ? cli
            : throw new InvalidOperationException($"No CLI adapter is registered for {provider}.");

    public async Task<IProviderCli> ResolveAsync(AiProvider provider, string? cliProviderId, CancellationToken ct = default)
    {
        if (provider != AiProvider.Custom)
            return Get(provider);

        if (string.IsNullOrWhiteSpace(cliProviderId))
            throw new InvalidOperationException("This agent uses a custom CLI but does not say which one.");

        var definition = await _definitions.GetAsync(cliProviderId, ct)
                         ?? throw new InvalidOperationException("That CLI provider no longer exists.");

        if (!definition.Enabled)
            throw new InvalidOperationException($"The '{definition.Name}' CLI provider is disabled.");

        // Built fresh each time so an edit to the definition takes effect on the next turn.
        return Build(definition);
    }

    public async Task<IReadOnlyList<IProviderCli>> GetAllAsync(CancellationToken ct = default)
    {
        var definitions = await _definitions.GetAllAsync(ct);

        return All
            .Concat(definitions.Where(d => d.Enabled).Select(Build))
            .ToList();
    }

    private CustomCli Build(CliProvider definition) =>
        new(definition, _runner, _options, _loggerFactory.CreateLogger<CustomCli>());
}
