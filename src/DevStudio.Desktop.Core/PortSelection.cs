using System.Net;
using System.Net.Sockets;

namespace DevStudio.Desktop;

/// <summary>
/// Which port the server child listens on.
///
/// The subtlety is that a port is not one thing. IPv4 and IPv6 are separate sockets, so something
/// already listening on <c>::1:7080</c> — a devStudio container published on the same port is the
/// obvious one — leaves <c>127.0.0.1:7080</c> free, and a server that only asks about IPv4 takes
/// it. Nothing collides and nothing complains. But the two now share a port number, and
/// <c>localhost</c> resolves to ::1 first on Windows: everything that addresses this server by name
/// — the built-in MCP server handed to every agent, above all — reaches the other one instead, and
/// is answered by a different build reading different data.
///
/// So a port counts as free only when it is free on both.
/// </summary>
public static class PortSelection
{
    /// <summary>
    /// The preferred port when it is free, so a bookmark keeps working and the documented port
    /// stays right. Anything else free otherwise, rather than refusing to start.
    /// </summary>
    public static int Choose(int preferred)
    {
        if (IsFree(preferred))
            return preferred;

        // The kernel hands out an IPv4 port here, which says nothing about the same number on IPv6,
        // so each candidate is checked before it is used. A handful of attempts is plenty: this is
        // an unlucky collision, not a search.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var candidate = Ephemeral();

            if (IsFree(candidate))
                return candidate;
        }

        // Every candidate was taken on the other family too. Starting on a port that at least this
        // process can bind beats not starting at all.
        return Ephemeral();
    }

    /// <summary>Free on both loopback addresses, not just the one the server binds.</summary>
    public static bool IsFree(int port) =>
        CanBind(IPAddress.Loopback, port) && CanBind(IPAddress.IPv6Loopback, port);

    private static int Ephemeral()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool CanBind(IPAddress address, int port)
    {
        // A machine with IPv6 switched off cannot bind ::1, and cannot be reached on it either, so
        // there is nothing there to be confused with.
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6)
            return true;

        try
        {
            var listener = new TcpListener(address, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
