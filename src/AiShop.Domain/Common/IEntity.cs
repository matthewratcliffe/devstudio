namespace AiShop.Domain.Common;

/// <summary>Anything the JSON stores can persist. Id is assigned on creation and never changes.</summary>
public interface IEntity
{
    string Id { get; set; }
    DateTimeOffset CreatedAt { get; set; }
    DateTimeOffset UpdatedAt { get; set; }
}

public abstract class Entity : IEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
