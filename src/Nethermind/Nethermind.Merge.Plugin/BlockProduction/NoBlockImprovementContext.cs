// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.BlockProduction;

public class NoBlockImprovementContext : IBlockImprovementContext
{
    public NoBlockImprovementContext(Block? currentBestBlock, UInt256 blockFees, DateTimeOffset startDateTime)
    {
        CurrentBestBlock = currentBestBlock;
        BlockFees = blockFees;
        StartDateTime = startDateTime;

        Disposed = true;
        ImprovementTask = Task.FromResult(currentBestBlock);
    }

    void IDisposable.Dispose() { }
    public bool Disposed { get; }

    public Block? CurrentBestBlock { get; }
    public UInt256 BlockFees { get; }
    public Task<Block?> ImprovementTask { get; }
    public DateTimeOffset StartDateTime { get; }
}
