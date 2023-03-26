// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Evm.Tracing.GethStyle;

public class GethLikeTxFileTracer : GethLikeTxTracer<GethTxFileTraceEntry>
{
    private readonly Action<GethTxFileTraceEntry> _dumpCallback;
    private long? _startGas;

    public GethLikeTxFileTracer(Action<GethTxFileTraceEntry> dumpCallback, GethTraceOptions options) : base(options)
    {
        _dumpCallback = dumpCallback ?? throw new ArgumentNullException(nameof(dumpCallback));

        IsTracingMemory = true;
        IsTracingOpLevelStorage = false;
        IsTracingRefunds = true;
    }

    public override GethLikeTxTrace BuildResult()
    {
        var trace = base.BuildResult();

        if (_startGas.HasValue)
            trace.Gas = _startGas.Value - CurrentTraceEntry.Gas;

        return trace;
    }

    public override void ReportRefund(long refund) => CurrentTraceEntry.Refund = refund;

    protected override void AddTraceEntry(GethTxFileTraceEntry entry)
    {
        _dumpCallback(entry);

        if (_startGas == null)
            _startGas = entry.Gas;
    }

    protected override GethTxFileTraceEntry CreateTraceEntry(Instruction opcode) => new() { OpcodeRaw = opcode };
}
