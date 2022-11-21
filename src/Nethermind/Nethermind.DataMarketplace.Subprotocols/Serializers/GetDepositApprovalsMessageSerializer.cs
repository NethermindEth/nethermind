// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Subprotocols.Serializers
{
    public class GetDepositApprovalsMessageSerializer : IMessageSerializer<GetDepositApprovalsMessage>
    {
        public byte[] Serialize(GetDepositApprovalsMessage message)
            => Rlp.Encode(Rlp.Encode(message.DataAssetId),
                Rlp.Encode(message.OnlyPending)).Bytes;

        public GetDepositApprovalsMessage Deserialize(byte[] bytes)
        {
            var context = bytes.AsRlpStream();
            context.ReadSequenceLength();
            var dataAssetId = context.DecodeKeccak();
            var onlyPending = context.DecodeBool();

            return new GetDepositApprovalsMessage(dataAssetId, onlyPending);
        }
    }
}
