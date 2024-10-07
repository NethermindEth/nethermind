// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.BlockProduction;

public class NoBlockProductionContext : IBlockProductionContext
{
    public NoBlockProductionContext(Block? currentBestBlock, UInt256 blockFees)
    {
        CurrentBestBlock = currentBestBlock;
        BlockFees = blockFees;
    }

    public Block? CurrentBestBlock { get; }
    public UInt256 BlockFees { get; }
}
