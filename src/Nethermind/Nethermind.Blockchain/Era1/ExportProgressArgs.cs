// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using MathNet.Numerics.LinearAlgebra.Factorization;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Blockchain.Era1;
public class ExportProgressArgs : EventArgs
{
    public ExportProgressArgs(long totalBlocks, long blockProcessed, long txProcessed, TimeSpan sinceLast, TimeSpan elapsed)
    {
        TotalBlocks = totalBlocks;
        BlockProcessedSinceLast = blockProcessed;
        TxProcessedSinceLast = txProcessed;
        ElapsedSinceLast = sinceLast;
        Elapsed = elapsed;
    }

    public long TotalBlocks { get; }
    public long BlockProcessedSinceLast { get; }
    public long TxProcessedSinceLast { get; }
    public TimeSpan ElapsedSinceLast { get; }
    public TimeSpan Elapsed { get; }
}
