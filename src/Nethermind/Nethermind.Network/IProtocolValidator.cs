using Nethermind.Network.P2P;

namespace Nethermind.Network
{
    public interface IProtocolValidator
    {
        bool DisconnectOnInvalid(string protocol, IP2PSession session, ProtocolInitializedEventArgs eventArgs);
    }
}