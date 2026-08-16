using System.Net;
using System.Net.Sockets;
using DevStudio.Desktop;

namespace DevStudio.Tests;

/// <summary>
/// A port number is not one socket. Something holding ::1:7080 leaves 127.0.0.1:7080 free, and a
/// server that takes it ends up sharing the number with whatever that other thing is — which then
/// answers everything addressed to <c>localhost</c>, because that is the address ::1 resolves from
/// first. The built-in MCP server is handed to agents by address, so this is the difference between
/// an agent reaching this orchestrator and an agent reaching a different one.
/// </summary>
public class PortSelectionTests
{
    [Fact]
    public void A_port_held_on_ipv6_only_is_not_free()
    {
        if (!Socket.OSSupportsIPv6)
            return;

        var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            // Nothing is on 127.0.0.1 here — this is exactly the shape a published container has.
            Assert.True(CanBindIpv4(port));
            Assert.False(PortSelection.IsFree(port));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void A_port_held_on_ipv4_only_is_not_free()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            Assert.False(PortSelection.IsFree(((IPEndPoint)listener.LocalEndpoint).Port));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void The_preferred_port_is_kept_when_it_is_free_on_both()
    {
        // Borrow a port and hand it straight back, so it is one the kernel considers available.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var free = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        Assert.Equal(free, PortSelection.Choose(free));
    }

    [Fact]
    public void A_preferred_port_held_on_ipv6_is_given_up_rather_than_shared()
    {
        if (!Socket.OSSupportsIPv6)
            return;

        var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
        listener.Start();

        try
        {
            var taken = ((IPEndPoint)listener.LocalEndpoint).Port;
            var chosen = PortSelection.Choose(taken);

            Assert.NotEqual(taken, chosen);
            Assert.True(PortSelection.IsFree(chosen));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void Listening_on_the_local_network_is_never_less_fussy_than_loopback()
    {
        // The wildcard bind is checked as well when the setting is on. Whether that catches a
        // conflict depends on the platform — Windows lets 0.0.0.0 and a specific address share a
        // port, Linux does not — but it can only ever rule a port out, never back in.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            Assert.False(PortSelection.IsFree(port));
            Assert.False(PortSelection.IsFree(port, allInterfaces: true));
        }
        finally
        {
            listener.Stop();
        }
    }

    private static bool CanBindIpv4(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
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
