// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Synchronization;

public class ImportFinishedArgs : ImportProgressChangedArgs
{
    public ImportFinishedArgs(
        TimeSpan elapsed,
        long blocksProcessed,
        long txProcessed,
        long totalBlocks,
        long epochsProcessed,
        long totalEpochs) :
        base(elapsed, blocksProcessed, txProcessed, totalBlocks, epochsProcessed, totalEpochs)
    {
    }
}
public class ImportProgressChangedArgs : EventArgs
{
    public ImportProgressChangedArgs(
        TimeSpan elapsed,
        long blocksProcessed,
        long txProcessed,
        long totalBlocks,
        long epochsProcessed,
        long totalEpochs)
    {
        Elapsed = elapsed;
        BlocksProcessed = blocksProcessed;
        TxProcessed = txProcessed;
        TotalBlocks = totalBlocks;
        EpochsProcessed = epochsProcessed;
        TotalEpochs = totalEpochs;
    }
    public TimeSpan Elapsed { get; }
    public long BlocksProcessed { get; }
    public long TxProcessed { get; }
    public long TotalBlocks { get; }
    public long EpochsProcessed { get; }
    public long TotalEpochs { get; }
}
