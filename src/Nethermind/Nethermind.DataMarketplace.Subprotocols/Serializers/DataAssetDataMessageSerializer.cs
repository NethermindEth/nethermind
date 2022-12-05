// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Subprotocols.Serializers
{
    public class DataAssetDataMessageSerializer : IMessageSerializer<DataAssetDataMessage>
    {
        public byte[] Serialize(DataAssetDataMessage assetDataMessage)
            => Rlp.Encode(
                Rlp.Encode(assetDataMessage.DepositId),
                Rlp.Encode(assetDataMessage.Client),
                Rlp.Encode(assetDataMessage.Data),
                Rlp.Encode(assetDataMessage.ConsumedUnits)
            ).Bytes;

        public DataAssetDataMessage Deserialize(byte[] bytes)
        {
            var context = bytes.AsRlpStream();
            context.ReadSequenceLength();
            Keccak depositId = context.DecodeKeccak();
            if (depositId == null)
            {
                throw new InvalidDataException($"{nameof(DataAssetDataMessage)}.{nameof(DataAssetDataMessage.DepositId)} cannot be null");
            }

            var client = context.DecodeString();
            var data = context.DecodeString();
            var usage = context.DecodeUInt();

            return new DataAssetDataMessage(depositId, client, data, usage);
        }
    }
}
