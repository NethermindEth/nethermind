using Nethermind.Network.P2P;

namespace Nethermind.Network
{
    public interface IProtocolValidator
    {
        bool DisconnectOnInvalid(string protocol, ISession session, ProtocolInitializedEventArgs eventArgs);
    }
}