// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Wit.Messages
{
    public class GetBlockWitnessHashesMessage : P2PMessage
    {
        public override int PacketType { get; } = WitMessageCode.GetBlockWitnessHashes;
        public override string Protocol { get; } = "wit";

        public long RequestId { get; set; }
        public Keccak BlockHash { get; set; }

        public GetBlockWitnessHashesMessage(long requestId, Keccak blockHash)
        {
            RequestId = requestId;
            BlockHash = blockHash;
        }
    }
}
