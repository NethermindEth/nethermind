// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Resettables;
using Nethermind.Evm.Tracing;
using Nethermind.PatternAnalyzer.Plugin.Types;
using Nethermind.StatsAnalyzer.Plugin.Analyzer;

namespace Nethermind.StatsAnalyzer.Plugin.Tracer;

public abstract class StatsAnalyzerTxTracer<TData, TStat, TTrace>(
    ResettableList<TData> buffer,
    IStatsAnalyzer<TData, TStat> statsAnalyzer,
    SortOrder sort,
    CancellationToken ct)
    : TxTracer, IStatsAnalyzerTxTracer<TTrace>
{
    protected readonly ResettableList<TData> Buffer = buffer;
    protected readonly CancellationToken Ct = ct;
    protected readonly SortOrder Sort = sort;
    protected readonly IStatsAnalyzer<TData, TStat> StatsAnalyzer = statsAnalyzer;
    protected StatsProcessingQueue<TData, TStat>? Queue = new(buffer, statsAnalyzer, ct);

    public abstract TTrace BuildResult(long fromBlock = 0, long toBlock = 0);

    protected void Build()
    {
        using (var q = Queue)
        {
            Queue = null;
            Queue = new StatsProcessingQueue<TData, TStat>(Buffer, StatsAnalyzer, Ct);
            q?.Dispose();
        }
    }
}
