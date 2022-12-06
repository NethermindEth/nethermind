// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Subprotocols.Serializers
{
    public class DataDeliveryReceiptMessageSerializer : IMessageSerializer<DataDeliveryReceiptMessage>
    {
        public byte[] Serialize(DataDeliveryReceiptMessage message)
            => Rlp.Encode(Rlp.Encode(message.DepositId),
                Rlp.Encode(message.Receipt)).Bytes;

        public DataDeliveryReceiptMessage Deserialize(byte[] bytes)
        {
            var context = bytes.AsRlpStream();
            context.ReadSequenceLength();
            var depositId = context.DecodeKeccak();
            var receipt = Rlp.Decode<DataDeliveryReceipt>(context);

            return new DataDeliveryReceiptMessage(depositId, receipt);
        }
    }
}
