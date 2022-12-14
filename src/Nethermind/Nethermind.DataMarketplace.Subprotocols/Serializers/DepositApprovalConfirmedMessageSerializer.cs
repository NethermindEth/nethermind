// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Subprotocols.Serializers
{
    public class DepositApprovalConfirmedMessageSerializer : IMessageSerializer<DepositApprovalConfirmedMessage>
    {
        public byte[] Serialize(DepositApprovalConfirmedMessage message)
            => Rlp.Encode(Rlp.Encode(message.DataAssetId),
                Rlp.Encode(message.Consumer)).Bytes;

        public DepositApprovalConfirmedMessage Deserialize(byte[] bytes)
        {
            var context = bytes.AsRlpStream();
            context.ReadSequenceLength();
            var dataAssetId = context.DecodeKeccak();
            var consumer = context.DecodeAddress();

            return new DepositApprovalConfirmedMessage(dataAssetId, consumer);
        }
    }
}
