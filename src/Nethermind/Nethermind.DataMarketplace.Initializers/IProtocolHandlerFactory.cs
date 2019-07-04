using Nethermind.Network.P2P;

namespace Nethermind.DataMarketplace.Initializers
{
    public interface IProtocolHandlerFactory
    {
        IProtocolHandler Create(ISession session);
    }
}