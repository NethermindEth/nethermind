// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    [DebuggerDisplay("{StartBlockHash} {MaxHeaders} {Skip} {Reverse}")]
    public class GetBlockHeadersMessage : P2PMessage
    {
        public override int PacketType { get; } = Eth62MessageCode.GetBlockHeaders;
        public override string Protocol { get; } = "eth";

        public long StartBlockNumber { get; set; }
        public Keccak? StartBlockHash { get; set; }
        public long MaxHeaders { get; set; }
        public long Skip { get; set; }
        public byte Reverse { get; set; }

        public override string ToString()
            => $"{nameof(GetBlockHeadersMessage)}({StartBlockNumber}|{StartBlockHash}, {MaxHeaders})";
    }
}
