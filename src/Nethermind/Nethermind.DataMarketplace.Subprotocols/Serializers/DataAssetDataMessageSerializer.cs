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
using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Network;

namespace Nethermind.DataMarketplace.Subprotocols.Serializers
{
    public class DataAssetDataMessageSerializer : IMessageSerializer<DataAssetDataMessage>
    {
        public byte[] Serialize(DataAssetDataMessage assetDataMessage)
            => Nethermind.Core.Encoding.Rlp.Encode(
                Nethermind.Core.Encoding.Rlp.Encode(assetDataMessage.DepositId),
                Nethermind.Core.Encoding.Rlp.Encode(assetDataMessage.Client),
                Nethermind.Core.Encoding.Rlp.Encode(assetDataMessage.Data),
                Nethermind.Core.Encoding.Rlp.Encode(assetDataMessage.ConsumedUnits)
            ).Bytes;

        public DataAssetDataMessage Deserialize(byte[] bytes)
        {
            var context = bytes.AsRlpStream();
            context.ReadSequenceLength();
            var depositId = context.DecodeKeccak();
            var client = context.DecodeString();
            var data = context.DecodeString();
            var usage = context.DecodeUInt();

            return new DataAssetDataMessage(depositId, client, data, usage);
        }
    }
}