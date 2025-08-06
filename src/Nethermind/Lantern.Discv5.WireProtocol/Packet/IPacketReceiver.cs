using Lantern.Discv5.Enr;
using Lantern.Discv5.WireProtocol.Messages.Responses;

namespace Lantern.Discv5.WireProtocol.Packet;

public interface IPacketReceiver
{

    Task<PongMessage?> SendPingAsync(IEnr dest);

    Task<IEnr[]?> SendFindNodeAsync(IEnr dest, byte[] targetNodeId);

    Task<IEnr[]?> SendFindNodeAsync(IEnr dest, int[] distances);

    void RaisePongResponseReceived(PongResponseEventArgs e);

    void RaiseNodesResponseReceived(NodesResponseEventArgs e);
}
