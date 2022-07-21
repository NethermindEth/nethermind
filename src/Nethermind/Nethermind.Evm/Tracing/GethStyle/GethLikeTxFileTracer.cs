//  Copyright (c) 2022 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;

namespace Nethermind.Evm.Tracing.GethStyle;

public class GethLikeTxFileTracer : GethLikeTxTracer<GethTxFileTraceEntry>
{
    private readonly Action<GethTxFileTraceEntry> _dumpCallback;
    private long? _startGas;

    public GethLikeTxFileTracer(Action<GethTxFileTraceEntry> dumpCallback, GethTraceOptions options) : base(options)
    {
        _dumpCallback = dumpCallback ?? throw new ArgumentNullException(nameof(dumpCallback));

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
