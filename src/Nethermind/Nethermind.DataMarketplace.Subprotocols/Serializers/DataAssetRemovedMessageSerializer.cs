// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Subprotocols.Serializers
{
    public class DataAssetRemovedMessageSerializer : IMessageSerializer<DataAssetRemovedMessage>
    {
        public byte[] Serialize(DataAssetRemovedMessage message)
            => Rlp.Encode(Rlp.Encode(message.DataAssetId)).Bytes;

        public DataAssetRemovedMessage Deserialize(byte[] bytes)
        {
            var context = bytes.AsRlpStream();
            context.ReadSequenceLength();
            var dataAssetId = context.DecodeKeccak();

            return new DataAssetRemovedMessage(dataAssetId);
        }
    }
}
