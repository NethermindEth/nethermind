// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.DataMarketplace.Subprotocols.Messages
{
    public class GetDepositApprovalsMessage : P2PMessage
    {
        public override int PacketType { get; } = NdmMessageCode.GetDepositApprovals;
        public override string Protocol => "ndm";
        public Keccak? DataAssetId { get; }
        public bool OnlyPending { get; }

        public GetDepositApprovalsMessage(Keccak? dataAssetId = null, bool onlyPending = false)
        {
            DataAssetId = dataAssetId;
            OnlyPending = onlyPending;
        }
    }
}
