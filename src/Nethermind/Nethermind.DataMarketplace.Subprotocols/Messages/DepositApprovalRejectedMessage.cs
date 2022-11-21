// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.DataMarketplace.Subprotocols.Messages
{
    public class DepositApprovalRejectedMessage : P2PMessage
    {
        public override int PacketType { get; } = NdmMessageCode.DepositApprovalRejected;
        public override string Protocol => "ndm";
        public Keccak DataAssetId { get; }
        public Address Consumer { get; }

        public DepositApprovalRejectedMessage(Keccak dataAssetId, Address consumer)
        {
            DataAssetId = dataAssetId;
            Consumer = consumer;
        }
    }
}
