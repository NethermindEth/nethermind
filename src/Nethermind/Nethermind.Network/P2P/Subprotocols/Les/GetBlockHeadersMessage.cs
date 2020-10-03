namespace Nethermind.Network.P2P.Subprotocols.Les
{
    public class GetBlockHeadersMessage: P2PMessage
    {
        public override int PacketType { get; } = LesMessageCode.GetBlockHeaders;
        public override string Protocol { get; } = P2P.Protocol.Les;
        public long RequestId;
        public Eth.V62.GetBlockHeadersMessage EthMessage;

        public GetBlockHeadersMessage()
        { 
        }
        
        public GetBlockHeadersMessage(Eth.V62.GetBlockHeadersMessage ethMessage, long requestId)
        {
            EthMessage = ethMessage;
            RequestId = requestId;
        }
    }
}
