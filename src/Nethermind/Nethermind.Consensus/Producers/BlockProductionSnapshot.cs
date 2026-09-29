// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Consensus.Producers;

/// <summary>An immutable block candidate paired with the fees it collected.</summary>
public sealed class BlockProductionSnapshot(Block? currentBestBlock, UInt256 blockFees) : IBlockProductionContext
{
    public Block? CurrentBestBlock { get; } = currentBestBlock;
    public UInt256 BlockFees { get; } = blockFees;
}
