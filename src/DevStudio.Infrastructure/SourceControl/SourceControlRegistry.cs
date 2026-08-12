using DevStudio.Application.Abstractions;
using DevStudio.Domain.Providers;

namespace DevStudio.Infrastructure.SourceControl;

public sealed class SourceControlRegistry : ISourceControlRegistry
{
    private readonly Dictionary<SourceControlProvider, ISourceControlCli> _byProvider;

    public SourceControlRegistry(IEnumerable<ISourceControlCli> clis)
    {
        _byProvider = clis.ToDictionary(c => c.Provider);
        All = _byProvider.Values.OrderBy(c => c.Provider).ToList();
    }

    public IReadOnlyList<ISourceControlCli> All { get; }

    public ISourceControlCli Get(SourceControlProvider provider) =>
        _byProvider.TryGetValue(provider, out var cli)
            ? cli
            : throw new InvalidOperationException($"No CLI is registered for {provider}.");
}
