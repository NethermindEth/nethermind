using Nethermind.Network.P2P;
using Session = Nethermind.DataMarketplace.Core.Domain.Session;

namespace Nethermind.DataMarketplace.Subprotocols.Messages
{
    public class SessionFinishedMessage : P2PMessage
    {
        public override int PacketType { get; } = NdmMessageCode.SessionFinished;
        public override string Protocol => "ndm";
        public Session Session { get; set; }

        public SessionFinishedMessage(Session session)
        {
            Session = session;
        }
    }
}