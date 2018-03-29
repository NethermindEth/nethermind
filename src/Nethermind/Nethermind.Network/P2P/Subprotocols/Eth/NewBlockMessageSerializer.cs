namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public class NewBlockMessageSerializer : IMessageSerializer<NewBlockMessage>
    {
        public byte[] Serialize(NewBlockMessage message, IMessagePad pad = null)
        {
            throw new System.NotImplementedException();
        }

        public NewBlockMessage Deserialize(byte[] bytes)
        {
            throw new System.NotImplementedException();
        }
    }
}