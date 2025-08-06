using System.Net;
using System.Net.Sockets;
using Lantern.Discv5.WireProtocol.Logging.Exceptions;

namespace Lantern.Discv5.WireProtocol.Utility;

public static class IpUtility
{
    public static IPAddress GetLocalIpAddress()
    {
        var host = Dns.GetHostEntry(Dns.GetHostName());

        foreach (var ip in host.AddressList)
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                return ip;
            }
        }

        throw new LocalIpAddressNotFoundException("No local IPv4 address found.");
    }
}