// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Subprotocols.Serializers
{
    public class DataAssetsMessageSerializer : IMessageSerializer<DataAssetsMessage>
    {
        public byte[] Serialize(DataAssetsMessage message)
            => Rlp.Encode(message.DataAssets).Bytes;

        public DataAssetsMessage Deserialize(byte[] bytes)
        {
            DataAsset[] dataAssets;
            try
            {
                dataAssets = Rlp.DecodeArray<DataAsset>(bytes.AsRlpStream());
                foreach (var dataAsset in dataAssets)
                {
                    dataAsset.ClearPlugin();
                }
            }
            catch (RlpException)
            {
                throw new InvalidDataException();
            }

            return new DataAssetsMessage(dataAssets);
        }
    }
}
