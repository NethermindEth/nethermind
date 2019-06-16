using Nethermind.Core.Extensions;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Network;

namespace Nethermind.DataMarketplace.Subprotocols.Serializers
{
    public class SessionFinishedMessageSerializer : IMessageSerializer<SessionFinishedMessage>
    {
        public byte[] Serialize(SessionFinishedMessage message)
            => Nethermind.Core.Encoding.Rlp.Encode(Nethermind.Core.Encoding.Rlp.Encode(message.Session)).Bytes;

        public SessionFinishedMessage Deserialize(byte[] bytes)
        {
            var context = bytes.AsRlpContext();
            context.ReadSequenceLength();
            var session = Nethermind.Core.Encoding.Rlp.Decode<Session>(context);

            return new SessionFinishedMessage(session);
        }
    }
}