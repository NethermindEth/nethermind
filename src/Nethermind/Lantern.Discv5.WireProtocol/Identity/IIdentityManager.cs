using System.Net;
using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Identity;

namespace Lantern.Discv5.WireProtocol.Identity;

public interface IIdentityManager
{
    IIdentitySigner Signer { get; }

    IIdentityVerifier Verifier { get; }

    IEnr Record { get; }

    bool IsIpAddressAndPortSet();

    void UpdateIpAddressAndPort(IPEndPoint endpoint);
}