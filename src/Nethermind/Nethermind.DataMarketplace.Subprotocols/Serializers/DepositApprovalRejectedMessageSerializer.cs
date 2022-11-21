// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Subprotocols.Serializers
{
    public class DepositApprovalRejectedMessageSerializer : IMessageSerializer<DepositApprovalRejectedMessage>
    {
        public byte[] Serialize(DepositApprovalRejectedMessage message)
            => Rlp.Encode(Rlp.Encode(message.DataAssetId),
                Rlp.Encode(message.Consumer)).Bytes;

        public DepositApprovalRejectedMessage Deserialize(byte[] bytes)
        {
            RlpStream? context = bytes.AsRlpStream();
            context.ReadSequenceLength();
            Keccak? dataAssetId = context.DecodeKeccak();
            Address? consumer = context.DecodeAddress();

            return new DepositApprovalRejectedMessage(dataAssetId, consumer);
        }
    }
}
