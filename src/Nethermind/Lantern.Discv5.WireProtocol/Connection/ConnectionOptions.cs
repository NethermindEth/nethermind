using System.Net;

namespace Lantern.Discv5.WireProtocol.Connection;

public class ConnectionOptions
{
    public IPAddress? IpAddress { get; set; }
    public int UdpPort { get; set; } = 9000;
    public int ReceiveTimeoutMs { get; set; } = 1000;
    public int RequestTimeoutMs { get; set; } = 2000;
    public int CheckPendingRequestsDelayMs { get; set; } = 500;
    public int RemoveCompletedRequestsDelayMs { get; set; } = 1000;
}