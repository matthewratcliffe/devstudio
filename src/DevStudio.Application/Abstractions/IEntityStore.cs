using DevStudio.Domain.Common;

namespace DevStudio.Application.Abstractions;

/// <summary>
/// Persistence for one entity type. Backed by JSON files on a mounted volume — there is no database,
/// so the store is deliberately tiny: read everything, write everything, no querying.
/// </summary>
public interface IEntityStore<T> where T : class, IEntity
{
    Task<IReadOnlyList<T>> GetAllAsync(CancellationToken ct = default);
    Task<T?> GetAsync(string id, CancellationToken ct = default);
    Task<T> UpsertAsync(T entity, CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>Raised after any successful write so the UI can refresh without polling.</summary>
    event Action<T>? Changed;
}
