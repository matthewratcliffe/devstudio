namespace DevStudio.Domain.Globals;

/// <summary>
/// One environment variable handed to every AI CLI this install launches, so a credential a skill or
/// a hook needs is set once rather than copied onto every agent that might reach for it.
///
/// It is deliberately the weakest layer: a CLI definition, an agent, or the turn itself all override
/// a variable of the same name, and the machine's own environment is what it layers on top of.
/// </summary>
public sealed class SharedEnvironmentVariable
{
    /// <summary>Variable name as the CLI will see it, e.g. <c>FASTMAIL_API_TOKEN</c>.</summary>
    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    /// <summary>What it is for, so the next person knows what breaks if they remove it.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Off keeps the row and its value but stops it being passed to anything.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether this may travel to a remote instance. Off by default and deliberately so: a turn that
    /// runs on another machine would otherwise carry every secret here across the wire without
    /// anybody having decided that. A remote applies its own shared environment regardless, so the
    /// usual answer to "the remote needs this too" is to set it over there.
    /// </summary>
    public bool ShareWithRemote { get; set; }
}
