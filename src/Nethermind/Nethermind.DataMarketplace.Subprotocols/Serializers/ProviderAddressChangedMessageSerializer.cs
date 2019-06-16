using Nethermind.Core.Extensions;
using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Network;

namespace Nethermind.DataMarketplace.Subprotocols.Serializers
{
    public class ProviderAddressChangedMessageSerializer : IMessageSerializer<ProviderAddressChangedMessage>
    {
        public byte[] Serialize(ProviderAddressChangedMessage message)
            => Nethermind.Core.Encoding.Rlp.Encode(Nethermind.Core.Encoding.Rlp.Encode(message.Address)).Bytes;

        public ProviderAddressChangedMessage Deserialize(byte[] bytes)
        {
            var context = bytes.AsRlpContext();
            context.ReadSequenceLength();
            var address = context.DecodeAddress();

            return new ProviderAddressChangedMessage(address);
        }
    }
}