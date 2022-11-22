// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
                Rlp.Encode((int)message.State)).Bytes;

        public DataAssetStateChangedMessage Deserialize(byte[] bytes)
        {
            RlpStream context = bytes.AsRlpStream();
            context.ReadSequenceLength();
            Keccak? dataAssetId = context.DecodeKeccak();
            DataAssetState state = (DataAssetState)context.DecodeInt();

            return new DataAssetStateChangedMessage(dataAssetId, state);
        }
    }
}
