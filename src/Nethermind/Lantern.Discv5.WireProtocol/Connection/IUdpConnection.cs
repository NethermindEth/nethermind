using System.Net;
using System.Net.Sockets;

namespace Lantern.Discv5.WireProtocol.Connection;

public interface IUdpConnection
{
    Task SendAsync(byte[] data, IPEndPoint destination);

    Task ListenAsync(CancellationToken token = default);

    IAsyncEnumerable<UdpReceiveResult> ReadMessagesAsync(CancellationToken token = default);

    void Close();
}