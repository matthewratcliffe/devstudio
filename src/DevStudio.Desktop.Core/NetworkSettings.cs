using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevStudio.Desktop;

/// <summary>
/// Whether the server child listens on loopback only or on every interface.
///
/// Loopback is the default because a desktop install runs as the user, with their files and their
/// CLI logins, and nothing on the network should reach that by accident. Turning this on is how you
/// drive the app from a phone or another machine on the same LAN — the same thing the container
/// does by default, where publishing a port is already a deliberate act.
///
/// Kept in its own file rather than in <c>desktop.json</c>: that one holds the Windows shell's
/// window bounds, and this is read by every shell — including the Photino one, which has no window
/// bounds to save.
/// </summary>
public sealed record NetworkSettings
{
    /// <summary>Set to 1/true to force the setting on, or 0/false to force it off, ignoring the file.</summary>
    public const string OverrideVariable = "DEVSTUDIO_LISTEN_LOCAL_NETWORK";

    private static readonly JsonSerializerOptions Format = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>Bind <c>0.0.0.0</c> instead of <c>127.0.0.1</c>, so other machines can reach the app.</summary>
    [JsonPropertyName("listenOnLocalNetwork")]
    public bool ListenOnLocalNetwork { get; init; }

    public static string File => Path.Combine(DesktopPaths.DataRoot, "network.json");

    /// <summary>
    /// The setting in force. The environment variable wins over the file, so a headless or scripted
    /// launch does not have to write state to say how it wants to listen.
    /// </summary>
    public static NetworkSettings Load()
    {
        if (Override() is { } forced)
            return new NetworkSettings { ListenOnLocalNetwork = forced };

        try
        {
            return System.IO.File.Exists(File)
                ? JsonSerializer.Deserialize<NetworkSettings>(System.IO.File.ReadAllText(File), Format) ?? new NetworkSettings()
                : new NetworkSettings();
        }
        catch (Exception)
        {
            // A corrupt file should not stop the app starting, and loopback is the safe reading.
            return new NetworkSettings();
        }
    }

    /// <summary>True once the environment variable has decided, in which case the UI cannot change it.</summary>
    public static bool IsForcedByEnvironment => Override() is not null;

    public void Save()
    {
        Directory.CreateDirectory(DesktopPaths.DataRoot);
        System.IO.File.WriteAllText(File, JsonSerializer.Serialize(this, Format));
    }

    /// <summary>The address the server binds: every interface, or loopback alone.</summary>
    public string BindAddress => ListenOnLocalNetwork ? "0.0.0.0" : "127.0.0.1";

    /// <summary>
    /// Addresses another machine could use, for the shell to show when the setting is on. Only
    /// addresses on a live, non-loopback interface — a disconnected adapter's stale IP reaches
    /// nobody, and offering it reads as the feature being broken.
    /// </summary>
    public static IReadOnlyList<string> LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                .Where(nic => nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .Select(address => address.Address)
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                .Where(address => !IPAddress.IsLoopback(address))
                .Select(address => address.ToString())
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    private static bool? Override() => Environment.GetEnvironmentVariable(OverrideVariable) switch
    {
        null or "" => null,
        "1" or "true" or "TRUE" or "True" or "yes" or "on" => true,
        _ => false,
    };
}
