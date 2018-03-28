using Nethermind.Network.Rlpx;

namespace Nethermind.Network.P2P
{
    public interface IP2PSubprotocolHandler
    {
        int ProtocolType { get; }
        void HandleMessage(Packet packet);
        void Init();
    }
}