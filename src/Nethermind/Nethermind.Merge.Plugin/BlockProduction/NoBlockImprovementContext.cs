// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.BlockProduction;

public class NoBlockImprovementContext(Block? currentBestBlock, UInt256 blockFees, DateTimeOffset startDateTime)
    : NoBlockProductionContext(currentBestBlock, blockFees), IBlockImprovementContext
{
    void IDisposable.Dispose() { }

    public void CancelOngoingImprovements() { }

    public bool Disposed => true;

    public Task<Block?> ImprovementTask { get; } = Task.FromResult(currentBestBlock);
    public DateTimeOffset StartDateTime { get; } = startDateTime;
}
