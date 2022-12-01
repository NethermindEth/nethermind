// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.DataMarketplace.Subprotocols.Messages
{
    public class RequestDepositApprovalMessage : P2PMessage
    {
        public override int PacketType { get; } = NdmMessageCode.RequestDepositApproval;
        public override string Protocol => "ndm";
        public Keccak DataAssetId { get; }
        public Address Consumer { get; }
        public string Kyc { get; }

        public RequestDepositApprovalMessage(Keccak dataAssetId, Address consumer, string kyc)
        {
            DataAssetId = dataAssetId;
            Consumer = consumer;
            Kyc = kyc;
        }
    }
}
