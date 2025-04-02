// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.BlockProduction;

public class NoBlockProductionContext : IBlockProductionContext
{
    public NoBlockProductionContext(Block? currentBestBlock, UInt256 blockFees, CancellationTokenSource cts)
    {
        CurrentBestBlock = currentBestBlock;
        BlockFees = blockFees;
        CancellationTokenSource = cts;
    }

    public Block? CurrentBestBlock { get; }
    public UInt256 BlockFees { get; }
    public CancellationTokenSource CancellationTokenSource { get; }

    public void CancelOngoingImprovements() => CancellationTokenSource.Cancel();

    public void Dispose() { }
}
