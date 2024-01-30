// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using System;

namespace Nethermind.Synchronization;

public class ImportProgressChangedArgs : EventArgs
{
    public ImportProgressChangedArgs(
        TimeSpan elapsed,
        long totalBlocksProcessed,
        long txProcessed,
        long totalBlocks,
        long epochProcessed,
        long totalEpochs)
    {
        Elapsed = elapsed;
        TotalBlocksProcessed = totalBlocksProcessed;
        TxProcessed = txProcessed;
        TotalBlocks = totalBlocks;
        EpochProcessed = epochProcessed;
        TotalEpochs = totalEpochs;
    }

    public TimeSpan Elapsed { get; }
    public long TotalBlocksProcessed { get; }
    public long TxProcessed { get; }
    public long TotalBlocks { get; }
    public long EpochProcessed { get; }
    public long TotalEpochs { get; }
}
