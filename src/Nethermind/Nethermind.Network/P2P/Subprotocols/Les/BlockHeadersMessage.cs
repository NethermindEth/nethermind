namespace Nethermind.Network.P2P.Subprotocols.Les
{
    public class BlockHeadersMessage: P2PMessage
    {
        public override int PacketType { get; } = LesMessageCode.BlockHeaders;
        public override string Protocol { get; } = P2P.Protocol.Les;
        public Eth.V62.BlockHeadersMessage EthMessage { get; set; }
        public long RequestId { get; set; }
        public int BufferValue { get; set; }
        
        public BlockHeadersMessage() 
        {
        }
        public BlockHeadersMessage(Eth.V62.BlockHeadersMessage ethMessage, long requestId, int bufferValue)
        {
            EthMessage = ethMessage;
            RequestId = requestId;
            BufferValue = bufferValue;
        }
    }
}
