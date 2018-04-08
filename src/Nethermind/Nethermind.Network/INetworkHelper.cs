using System.Net;

namespace Nethermind.Network
{
    public interface INetworkHelper
    {
        IPAddress GetLocalIp();
        IPAddress GetExternalIp();
    }
}