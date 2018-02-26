using Nevermind.Core.Encoding;

namespace Nevermind.Network.P2P
{
    public class PingMessageSerializer : IMessageSerializer<PingMessage>
    {
        public byte[] Serialize(PingMessage message, IMessagePad pad = null)
        {
            return Rlp.OfEmptySequence.Bytes;
        }

        public PingMessage Deserialize(byte[] bytes)
        {
            return PingMessage.Instance;
        }
    }
}