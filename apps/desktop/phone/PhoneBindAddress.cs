using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Sovereign.Desktop.Phone;

public enum PhoneBindMode { Loopback, Tailscale }

/// <summary>Chooses the single address phone access may listen on. Port 8789 is the xAI Desk port. It starts off.</summary>
public static class PhoneBindAddress
{
    public const int Port = 8789;

    public static bool IsTailscale(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127;
    }

    public static bool IsLoopbackV4(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork && IPAddress.IsLoopback(address);

    public static IPAddress? Select(PhoneBindMode mode, IEnumerable<IPAddress> addresses)
    {
        if (mode == PhoneBindMode.Loopback) return IPAddress.Loopback;
        foreach (var address in addresses)
            if (IsTailscale(address)) return address;
        return null;
    }

    public static bool ConnectionAllowed(IPAddress? localAddress, PhoneBindMode mode)
    {
        if (localAddress is null) return false;
        return mode == PhoneBindMode.Loopback ? IsLoopbackV4(localAddress) : IsTailscale(localAddress);
    }

    public static IReadOnlyList<IPAddress> MachineAddresses()
    {
        var found = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork) found.Add(unicast.Address);
        }
        return found;
    }
}
