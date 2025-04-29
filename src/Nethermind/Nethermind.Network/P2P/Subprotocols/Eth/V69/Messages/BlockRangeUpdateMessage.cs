// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;

public class BlockRangeUpdateMessage : P2PMessage
{
    public override int PacketType { get; } = Eth69MessageCode.BlockRangeUpdate;
    public override string Protocol { get; } = "eth";

    public long EarliestBlock { get; set; }
    public long LatestBlock { get; set; }
    public required Hash256 LatestBlockHash { get; set; }
}
