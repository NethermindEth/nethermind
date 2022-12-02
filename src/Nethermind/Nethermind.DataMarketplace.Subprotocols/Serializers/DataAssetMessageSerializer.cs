// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Subprotocols.Serializers
{
    public class DataAssetMessageSerializer : IMessageSerializer<DataAssetMessage>
    {
        public byte[] Serialize(DataAssetMessage message)
            => Rlp.Encode(Rlp.Encode(message.DataAsset)).Bytes;

        public DataAssetMessage Deserialize(byte[] bytes)
        {
            var context = bytes.AsRlpStream();
            context.ReadSequenceLength();

            DataAsset dataAsset;
            try
            {
                dataAsset = Rlp.Decode<DataAsset>(context);
            }
            catch (RlpException)
            {
                throw new InvalidDataException("DataAssset cannot be null");
            }

            return new DataAssetMessage(dataAsset);
        }
    }
}
