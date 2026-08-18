using DevStudio.Domain.Agents;
using DevStudio.Domain.Workflows;

namespace DevStudio.Ui.Services;

/// <summary>Groups agents and workflows by category for pick lists, categories and items both A-Z.</summary>
public static class CategoryGrouping
{
    public const string Uncategorized = "Uncategorized";

    public static IEnumerable<IGrouping<string, Agent>> GroupedByCategory(this IEnumerable<Agent> agents) =>
        agents
            .GroupBy(a => string.IsNullOrWhiteSpace(a.Category) ? Uncategorized : a.Category.Trim())
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => (IGrouping<string, Agent>)new Grouping<Agent>(g.Key, g.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)));

    public static IEnumerable<IGrouping<string, Workflow>> GroupedByCategory(this IEnumerable<Workflow> workflows) =>
        workflows
            .GroupBy(w => string.IsNullOrWhiteSpace(w.Category) ? Uncategorized : w.Category.Trim())
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => (IGrouping<string, Workflow>)new Grouping<Workflow>(g.Key, g.OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase)));

    private sealed class Grouping<T> : IGrouping<string, T>
    {
        private readonly IEnumerable<T> _items;
        public string Key { get; }

        public Grouping(string key, IEnumerable<T> items)
        {
            Key = key;
            _items = items;
        }

        public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
