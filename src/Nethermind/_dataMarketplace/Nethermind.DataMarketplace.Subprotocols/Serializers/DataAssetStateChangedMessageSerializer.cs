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

using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Subprotocols.Serializers
{
    public class DataAssetStateChangedMessageSerializer : IMessageSerializer<DataAssetStateChangedMessage>
    {
        public byte[] Serialize(DataAssetStateChangedMessage message)
            => Rlp.Encode(Rlp.Encode(message.DataAssetId),
                Rlp.Encode((int) message.State)).Bytes;

        public DataAssetStateChangedMessage Deserialize(byte[] bytes)
        {
            RlpStream context = bytes.AsRlpStream();
            context.ReadSequenceLength();
            Keccak? dataAssetId = context.DecodeKeccak();
            DataAssetState state = (DataAssetState) context.DecodeInt();

            return new DataAssetStateChangedMessage(dataAssetId, state);
        }
    }
}
