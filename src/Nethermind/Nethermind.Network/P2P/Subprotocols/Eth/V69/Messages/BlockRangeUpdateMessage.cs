// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;

/// <summary>
/// With this message, the peer announces that all blocks <c>b</c> with <c>earliest >= b >= latest</c>
/// are available via <c>GetBlockBodies</c>, and also that receipts for these blocks are available via <c>GetReceipts</c>.
/// </summary>
public class BlockRangeUpdateMessage : P2PMessage
{
    public override int PacketType { get; } = Eth69MessageCode.BlockRangeUpdate;
    public override string Protocol { get; } = "eth";

    /// <summary>
    /// Number of the earliest available full block.
    /// </summary>
    public long EarliestBlock { get; set; }

    /// <summary>
    /// Number of the latest available full block number.
    /// </summary>
    public long LatestBlock { get; set; }

    /// <summary>
    /// Hash of the latest available full block.
    /// </summary>
    public required Hash256 LatestBlockHash { get; set; }

    public override string ToString() => $"{nameof(BlockRangeUpdateMessage)}({EarliestBlock},{LatestBlock},{LatestBlockHash})";
}
