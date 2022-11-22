// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class NewBlockMessage : P2PMessage
    {
        public override int PacketType { get; } = Eth62MessageCode.NewBlock;
        public override string Protocol { get; } = "eth";

        public Block Block { get; set; }
        public UInt256 TotalDifficulty { get; set; }

        public override string ToString() => $"{nameof(NewBlockMessage)}({Block})";
    }
}
