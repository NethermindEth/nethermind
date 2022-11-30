// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Subprotocols.Serializers
{
    public class DataRequestMessageMessageSerializer : IMessageSerializer<DataRequestMessage>
    {
        public byte[] Serialize(DataRequestMessage message)
            => Rlp.Encode(Rlp.Encode(message.DataRequest),
                Rlp.Encode(message.ConsumedUnits)).Bytes;

        public DataRequestMessage Deserialize(byte[] bytes)
        {
            var context = bytes.AsRlpStream();
            context.ReadSequenceLength();
            var dataRequest = Rlp.Decode<DataRequest>(context);
            var consumedUnits = context.DecodeUInt();

            return new DataRequestMessage(dataRequest, consumedUnits);
        }
    }
}
