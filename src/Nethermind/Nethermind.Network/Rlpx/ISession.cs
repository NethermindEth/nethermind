using Nethermind.Core.Crypto;
using Nethermind.Network.P2P;

namespace Nethermind.Network.Rlpx
{
    public interface ISession
    {
        PublicKey LocalNodeId { get; }
        int ListenPort { get; }
        void InitInbound(HelloMessage helloMessage);
        void InitOutbound();
        void HandlePing();
        void Disconnect(DisconnectReason disconnectReason);
        void Close(DisconnectReason disconnectReason);
        void HandlePong();
        void Ping();
    }
}