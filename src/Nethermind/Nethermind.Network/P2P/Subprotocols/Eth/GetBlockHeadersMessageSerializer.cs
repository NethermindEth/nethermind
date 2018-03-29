namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public class GetBlockHeadersMessageSerializer : IMessageSerializer<GetBlockHeadersMessage>
    {
        public byte[] Serialize(GetBlockHeadersMessage message, IMessagePad pad = null)
        {
            throw new System.NotImplementedException();
        }

        public GetBlockHeadersMessage Deserialize(byte[] bytes)
        {
            throw new System.NotImplementedException();
        }
    }
}