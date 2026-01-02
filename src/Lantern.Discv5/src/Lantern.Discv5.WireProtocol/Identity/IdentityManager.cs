using System.Net;
using System.Net.Sockets;
using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Entries;
using Lantern.Discv5.Enr.Identity;
using Lantern.Discv5.WireProtocol.Connection;
using Lantern.Discv5.WireProtocol.Session;
using Microsoft.Extensions.Logging;

namespace Lantern.Discv5.WireProtocol.Identity;

public class IdentityManager(SessionOptions sessionOptions, ConnectionOptions connectionOptions, IEnr enr, ILoggerFactory loggerFactory) : IIdentityManager
{
    private readonly ILogger<IdentityManager> _logger = loggerFactory.CreateLogger<IdentityManager>();

    public IIdentityVerifier Verifier => sessionOptions.Verifier!;

    public IIdentitySigner Signer => sessionOptions.Signer!;

    public IEnr Record => enr;

    public bool IsIpAddressAndPortSet()
    {
        return Record.HasKey(EnrEntryKey.Ip) && Record.HasKey(EnrEntryKey.Udp) || (Record.HasKey(EnrEntryKey.Ip6) && Record.HasKey(EnrEntryKey.Udp6));
    }

    public void UpdateIpAddressAndPort(IPEndPoint endpoint)
    {
        if (endpoint.AddressFamily == AddressFamily.InterNetwork)
        {
            Record.UpdateEntry(new EntryIp(endpoint.Address));
            Record.UpdateEntry(new EntryUdp(connectionOptions.UdpPort));
        }
        else if (endpoint.AddressFamily == AddressFamily.InterNetworkV6)
        {
            Record.UpdateEntry(new EntryIp6(endpoint.Address));
            Record.UpdateEntry(new EntryUdp6(connectionOptions.UdpPort));
        }

        Record.UpdateSignature();

        _logger.LogInformation("Self ENR updated => {Enr}", Record);
    }
}
