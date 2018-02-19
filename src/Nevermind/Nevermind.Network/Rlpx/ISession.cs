using Nevermind.Core.Crypto;
using Nevermind.Network.P2P;

namespace Nevermind.Network.Rlpx
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