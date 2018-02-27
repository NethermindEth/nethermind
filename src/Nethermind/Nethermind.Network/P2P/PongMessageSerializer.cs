using Nethermind.Core.Encoding;

namespace Nethermind.Network.P2P
{
    public class PongMessageSerializer : IMessageSerializer<PongMessage>
    {
        public byte[] Serialize(PongMessage message, IMessagePad pad = null)
        {
            return Rlp.OfEmptySequence.Bytes;
        }

        public PongMessage Deserialize(byte[] bytes)
        {
            return PongMessage.Instance;
        }
    }
}