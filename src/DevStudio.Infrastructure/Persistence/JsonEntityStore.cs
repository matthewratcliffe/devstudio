using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Domain.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevStudio.Infrastructure.Persistence;

/// <summary>
/// File-per-entity JSON persistence on the mounted volume. There is no database by design: state is
/// small, and keeping it as readable files means a volume backup is the whole backup.
/// </summary>
public sealed class JsonEntityStore<T> : IEntityStore<T> where T : class, IEntity
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ConcurrentDictionary<string, T> _cache = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _directory;
    private readonly ILogger _logger;
    private bool _loaded;

    public JsonEntityStore(IOptions<OrchestratorOptions> options, ILogger<JsonEntityStore<T>> logger)
    {
        _logger = logger;
        _directory = Path.Combine(options.Value.DataPath, CollectionName);
        Directory.CreateDirectory(_directory);
    }

    /// <summary>Folder name, e.g. <c>Agent</c> becomes <c>agents</c>.</summary>
    private static string CollectionName
    {
        get
        {
            var name = typeof(T).Name.ToLowerInvariant();

            return name switch
            {
                _ when name.EndsWith('y') => name[..^1] + "ies",
                // Already plural (GlobalSettings), so leave it alone rather than doubling the s.
                _ when name.EndsWith('s') => name,
                _ => name + "s",
            };
        }
    }

    public event Action<T>? Changed;

    public async Task<IReadOnlyList<T>> GetAllAsync(CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        return _cache.Values.OrderByDescending(e => e.CreatedAt).ToList();
    }

    public async Task<T?> GetAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        await EnsureLoadedAsync(ct);
        return _cache.TryGetValue(id, out var entity) ? entity : null;
    }

    public async Task<T> UpsertAsync(T entity, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);

        if (string.IsNullOrWhiteSpace(entity.Id))
            entity.Id = Guid.NewGuid().ToString("n");

        entity.UpdatedAt = DateTimeOffset.UtcNow;
        _cache[entity.Id] = entity;

        await _writeLock.WaitAsync(ct);
        try
        {
            var path = PathFor(entity.Id);
            var temp = path + ".tmp";
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, entity, SerializerOptions, ct);
            }
            // Replace atomically so a crash mid-write cannot leave a truncated file behind.
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            _writeLock.Release();
        }

        Changed?.Invoke(entity);
        return entity;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);

        if (!_cache.TryRemove(id, out var removed))
            return false;

        await _writeLock.WaitAsync(ct);
        try
        {
            var path = PathFor(id);
            if (File.Exists(path))
                File.Delete(path);
        }
        finally
        {
            _writeLock.Release();
        }

        Changed?.Invoke(removed);
        return true;
    }

    private string PathFor(string id) => Path.Combine(_directory, $"{Sanitise(id)}.json");

    private static string Sanitise(string id)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(id.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded)
            return;

        await _writeLock.WaitAsync(ct);
        try
        {
            if (_loaded)
                return;

            foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
            {
                try
                {
                    await using var stream = File.OpenRead(file);
                    var entity = await JsonSerializer.DeserializeAsync<T>(stream, SerializerOptions, ct);
                    if (entity is not null && !string.IsNullOrWhiteSpace(entity.Id))
                        _cache[entity.Id] = entity;
                }
                catch (Exception ex)
                {
                    // One corrupt file must not stop the whole app from starting.
                    _logger.LogWarning(ex, "Skipping unreadable state file {File}", file);
                }
            }

            _loaded = true;
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
