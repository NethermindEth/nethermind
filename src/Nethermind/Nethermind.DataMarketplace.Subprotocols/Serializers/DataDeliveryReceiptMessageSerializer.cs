//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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