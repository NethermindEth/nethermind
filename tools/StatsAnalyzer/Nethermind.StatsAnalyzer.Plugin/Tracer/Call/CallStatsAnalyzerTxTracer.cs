// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Resettables;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.StatsAnalyzer.Plugin.Analyzer.Call;
using Nethermind.StatsAnalyzer.Plugin.Types;

namespace Nethermind.StatsAnalyzer.Plugin.Tracer.Call;

public sealed class CallStatsAnalyzerTxTracer : StatsAnalyzerTxTracer<Address, CallStat, CallAnalyzerTxTrace>

{
    public CallStatsAnalyzerTxTracer(ResettableList<Address> buffer,
        CallStatsAnalyzer callStatsAnalyzer, SortOrder sort, CancellationToken ct) : base(buffer, callStatsAnalyzer,
        sort, ct)

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


    private static bool IsTrackedCallType(ExecutionType t) =>
        t is ExecutionType.CALL or ExecutionType.STATICCALL
          or ExecutionType.CALLCODE or ExecutionType.DELEGATECALL;

    public override void ReportAction(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input,
        ExecutionType callType, bool isPrecompileCall = false)
    {
        if (!isPrecompileCall && IsTrackedCallType(callType)) Queue?.Enqueue(to);
    }
}
