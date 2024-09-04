// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Evm.CodeAnalysis.IL;

internal class IlvmTxTrace(List<ChunkTraceEntry> ilvmTraceEntries)
{
    public List<ChunkTraceEntry> IlvmTrace { get; set; } = ilvmTraceEntries;
}

internal class ChunkTraceEntry
{
    public bool IsPrecompiled { get; set; }
    public int Gas { get; set; }
    public int PC { get; set; }
    public string SegmentID { get; set; }
}
internal class IlvmBlockTracer : BlockTracerBase<IlvmTxTrace, ILVMTxTracer>
{
    protected override IlvmTxTrace OnEnd(ILVMTxTracer txTracer)
    {
        return new IlvmTxTrace(txTracer.IlvmTraceEntries);
    }

    protected override ILVMTxTracer OnStart(Transaction? tx)
    {
        return new ILVMTxTracer();
    }
}

internal class ILVMTxTracer : TxTracer
{
    public List<ChunkTraceEntry> IlvmTraceEntries { get; set; } = new List<ChunkTraceEntry>();
    public override void ReportPredefinedPatternExecution(long gas, int pc, string segmentID)
    {
        IlvmTraceEntries.Add(new ChunkTraceEntry { Gas = (int)gas, PC = pc, SegmentID = segmentID, IsPrecompiled = false });
    }

    public override void ReportCompiledSegmentExecution(long gas, int pc, string segmentId)
    {
        IlvmTraceEntries.Add(new ChunkTraceEntry { Gas = (int)gas, PC = pc, SegmentID = segmentId, IsPrecompiled = true });
    }
}
