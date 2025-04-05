// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Resettables;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.PatternAnalyzer.Plugin.Analyzer.Call;
using Nethermind.PatternAnalyzer.Plugin.Types;
using Nethermind.StatsAnalyzer.Plugin.Analyzer;

namespace Nethermind.StatsAnalyzer.Plugin.Tracer.Call;

public sealed class CallStatsAnalyzerTxTracer : StatsAnalyzerTxTracer<Address,CallStat,CallAnalyzerTxTrace>

{

    public CallStatsAnalyzerTxTracer(ResettableList<Address> buffer,
        CallStatsAnalyzer callStatsAnalyzer, SortOrder sort, CancellationToken ct) : base(buffer, callStatsAnalyzer, sort, ct)

    {
        IsTracingActions = true;
    }



    public override CallAnalyzerTxTrace BuildResult(long fromBlock = 0, long toBlock = 0)
    {
        Build();
        CallAnalyzerTxTrace trace = new();
        trace.InitialBlockNumber = fromBlock;
        trace.CurrentBlockNumber = toBlock;
        var stats = StatsAnalyzer.Stats(Sort);

        foreach (var stat in stats)
        {
            var entry = new CallAnalyzerTraceEntry
            {
                Address = stat.Address.ToString(),
                Count = stat.Count
            };
            trace.Entries.Add(entry);
        }


        return trace;
    }


    public override void ReportAction(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input,
        ExecutionType callType, bool isPrecompileCall = false)
    {
        if (!isPrecompileCall && new[]
                    { ExecutionType.CALL, ExecutionType.STATICCALL, ExecutionType.CALLCODE, ExecutionType.DELEGATECALL }
                .Contains(callType)) Queue?.Enqueue(to);
    }
}
