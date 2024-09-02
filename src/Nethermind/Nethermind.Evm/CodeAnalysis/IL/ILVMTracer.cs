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

internal class IlvmTxTrace(List<IlvmTrace> ilvmTraceEntries)
{
    public List<IlvmTrace> IlvmTrace { get; set; } = ilvmTraceEntries;
}

internal abstract class IlvmTrace;
internal class ChunkTrace : IlvmTrace
{
    public int Start { get; set; }
    public int End { get; set; }
    public int Gas { get; set; }
    public int PC { get; set; }
    public string SegmentID { get; set; }
}

internal class SegmentTrace : IlvmTrace
{
    public int Gas { get; set; }
    public int PC { get; set; }
    public string SegmentID { get; set; }
}

internal class AnalysisTrace : IlvmTrace
{
    public IlInfo.ILMode Mode { get; set; }
    public bool IsStart { get; set; }
    public bool IsEnd => !IsStart;

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
    public List<IlvmTrace> IlvmTraceEntries { get; set; } = new List<IlvmTrace>();

    public override void ReportChunkAnalysisEnd()
    {
        IlvmTraceEntries.Add(new AnalysisTrace { Mode = IlInfo.ILMode.PatternMatching, IsStart = false });
    }

    public override void ReportChunkAnalysisStart()
    {
        IlvmTraceEntries.Add(new AnalysisTrace { Mode = IlInfo.ILMode.PatternMatching, IsStart = true });
    }

    public override void ReportChunkExecution(long gas, int pc, string segmentID)
    {
        IlvmTraceEntries.Add(new ChunkTrace { Gas = (int)gas, PC = pc, SegmentID = segmentID });
    }

    public override void ReportCompiledSegmentExecution(long gas, int pc, string segmentId)
    {
        IlvmTraceEntries.Add(new SegmentTrace { Gas = (int)gas, PC = pc, SegmentID = segmentId });
    }

    public override void ReportSegmentAnalysisEnd()
    {
        IlvmTraceEntries.Add(new AnalysisTrace { Mode = IlInfo.ILMode.SubsegmentsCompiling, IsStart = false });
    }

    public override void ReportSegmentAnalysisStart()
    {
        IlvmTraceEntries.Add(new AnalysisTrace { Mode = IlInfo.ILMode.SubsegmentsCompiling, IsStart = true });
    }
}
