using Nethermind.Network.Rlpx;

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public class EthHandler : IP2PSubprotocolHandler
    {
        private readonly IMessageSender _messageSender;

        public EthHandler(IMessageSender messageSender)
        {
            _messageSender = messageSender;
        }
        
        public int ProtocolType { get; } = 1;

        public void HandleMessage(Packet packet)
        {
            throw new System.NotImplementedException();
        }

        public void Init()
        {
            StatusMessage statusMessage = new StatusMessage();
            //
            
            _messageSender.Enqueue(statusMessage);
        }
    }
}