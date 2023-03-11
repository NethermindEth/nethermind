// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Wit.Messages
{
    public class BlockWitnessHashesMessage : P2PMessage
    {
        public override int PacketType { get; } = WitMessageCode.BlockWitnessHashes;

        public override string Protocol { get; } = "wit";

        public long RequestId { get; }

        public Keccak[] Hashes { get; }

        public BlockWitnessHashesMessage(long requestId, Keccak[] hashes)
        {
            RequestId = requestId;
            Hashes = hashes;
        }
    }
}
