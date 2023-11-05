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

        public Hash256[] Hashes { get; }

        public BlockWitnessHashesMessage(long requestId, Hash256[] hashes)
        {
            RequestId = requestId;
            Hashes = hashes;
        }
    }
}
