// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class GetBlockBodiesMessage(IReadOnlyList<Hash256> blockHashes) : P2PMessage
    {
        public IReadOnlyList<Hash256> BlockHashes { get; } = blockHashes;
        public override int PacketType => Eth62MessageCode.GetBlockBodies;
        public override string Protocol => "eth";

        public GetBlockBodiesMessage(params Hash256[] blockHashes) : this((IReadOnlyList<Hash256>)blockHashes)
        {
        }

        public override string ToString() => $"{nameof(GetBlockBodiesMessage)}({BlockHashes.Count})";
    }
}
