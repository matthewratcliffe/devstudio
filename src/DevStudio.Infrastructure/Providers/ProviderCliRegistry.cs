using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Domain.Providers;
using DevStudio.Infrastructure.Providers.Acp;
using DevStudio.Infrastructure.Providers.OpenAi;
using DevStudio.Infrastructure.Workspaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevStudio.Infrastructure.Providers;

public sealed class ProviderCliRegistry : IProviderCliRegistry
{
    private readonly Dictionary<AiProvider, IProviderCli> _byProvider;
    private readonly IEntityStore<CliProvider> _definitions;
    private readonly IProcessRunner _runner;
    private readonly IAcpConnectionFactory _acp;
    private readonly IHttpClientFactory _httpClients;
    private readonly ConversationStore _conversations;
    private readonly IImageGenerationService _images;
    private readonly OrchestratorOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly WorkspacePathPolicy _policy;

    public ProviderCliRegistry(
        IEnumerable<IProviderCli> clis,
        IEntityStore<CliProvider> definitions,
        IProcessRunner runner,
        IAcpConnectionFactory acp,
        IHttpClientFactory httpClients,
        ConversationStore conversations,
        IImageGenerationService images,
        IOptions<OrchestratorOptions> options,
        ILoggerFactory loggerFactory,
        WorkspacePathPolicy policy)
    {
        _byProvider = clis.ToDictionary(c => c.Provider);
        _definitions = definitions;
        _runner = runner;
        _acp = acp;
        _httpClients = httpClients;
        _conversations = conversations;
        _images = images;
        _options = options.Value;
        _loggerFactory = loggerFactory;
        _policy = policy;

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

    /// <summary>
    /// One definition, three ways of talking to it: run a command per turn, drive an ACP agent over
    /// its stdio, or run the tool loop ourselves against an HTTP endpoint.
    /// </summary>
    private IProviderCli Build(CliProvider definition) => definition.Transport switch
    {
        CliTransport.Acp => new AcpCli(definition, _acp, _options, _loggerFactory.CreateLogger<AcpCli>(), _policy),
        CliTransport.OpenAiCompatible => new OpenAiCompatibleCli(
            definition,
            _httpClients,
            _runner,
            _conversations,
            _images,
            _loggerFactory.CreateLogger<OpenAiCompatibleCli>(),
            _policy),
        _ => new CustomCli(definition, _runner, _options, _loggerFactory.CreateLogger<CustomCli>()),
    };
}
