namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public class BlockBodiesMessageSerializer : IMessageSerializer<BlockBodiesMessage>
    {
        public byte[] Serialize(BlockBodiesMessage message, IMessagePad pad = null)
        {
            throw new System.NotImplementedException();
        }

        public BlockBodiesMessage Deserialize(byte[] bytes)
        {
            throw new System.NotImplementedException();
        }
    }
}