// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.BlockProduction;

public class NoBlockProductionContext(Block? currentBestBlock, UInt256 blockFees) : IBlockProductionContext
{
    public Block? CurrentBestBlock { get; } = currentBestBlock;
    public UInt256 BlockFees { get; } = blockFees;
}
