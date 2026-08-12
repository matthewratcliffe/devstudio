using AiShop.Domain.Common;

namespace AiShop.Domain.Providers;

/// <summary>
/// Where each forge lives. Self-managed GitLab and GitHub Enterprise are common enough that the
/// hostname has to be changeable from the UI rather than only through configuration.
/// </summary>
public sealed class SourceControlSettings : Entity
{
    public const string WellKnownId = "sourcecontrol";

    /// <summary>Hostname per forge, keyed by <see cref="SourceControlProvider"/> name.</summary>
    public Dictionary<string, string> Hosts { get; set; } = [];

    public SourceControlSettings() => Id = WellKnownId;

    public string? GetHost(SourceControlProvider provider) =>
        Hosts.TryGetValue(provider.ToString(), out var host) && !string.IsNullOrWhiteSpace(host)
            ? host.Trim()
            : null;

    public void SetHost(SourceControlProvider provider, string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            Hosts.Remove(provider.ToString());
        else
            Hosts[provider.ToString()] = host.Trim();
    }
}
