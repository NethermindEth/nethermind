/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Core.Extensions;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Network;

namespace Nethermind.DataMarketplace.Subprotocols.Serializers
{
    public class EthRequestedMessageSerializer : IMessageSerializer<EthRequestedMessage>
    {
        public byte[] Serialize(EthRequestedMessage message)
            => Nethermind.Core.Encoding.Rlp.Encode(Nethermind.Core.Encoding.Rlp.Encode(message.Address),
                Nethermind.Core.Encoding.Rlp.Encode(message.Value),
                Nethermind.Core.Encoding.Rlp.Encode((int) message.Status)).Bytes;

        public EthRequestedMessage Deserialize(byte[] bytes)
        {
            var context = bytes.AsRlpContext();
            context.ReadSequenceLength();
            var address = context.DecodeAddress();
            var value = context.DecodeUInt256();
            var status = (FaucetRequestStatus) context.DecodeInt();

            return new EthRequestedMessage(address, value, status);
        }
    }
}