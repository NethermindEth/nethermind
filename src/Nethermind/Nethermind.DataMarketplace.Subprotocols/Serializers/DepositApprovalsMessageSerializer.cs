// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Subprotocols.Serializers
{
    public class DepositApprovalsMessageSerializer : IMessageSerializer<DepositApprovalsMessage>
    {
        public byte[] Serialize(DepositApprovalsMessage message)
            => Rlp.Encode(message.DepositApprovals).Bytes;

        public DepositApprovalsMessage Deserialize(byte[] bytes)
            => new DepositApprovalsMessage(Rlp
                .DecodeArray<DepositApproval>(bytes.AsRlpStream()));
    }
}
