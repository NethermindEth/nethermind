namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public class BlockHeadersMessageSerializer : IMessageSerializer<BlockHeadersMessage>
    {
        public byte[] Serialize(BlockHeadersMessage message, IMessagePad pad = null)
        {
            throw new System.NotImplementedException();
        }

        public BlockHeadersMessage Deserialize(byte[] bytes)
        {
            throw new System.NotImplementedException();
        }
    }
}