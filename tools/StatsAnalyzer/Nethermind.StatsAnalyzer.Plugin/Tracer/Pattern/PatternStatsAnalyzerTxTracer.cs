// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Resettables;
using Nethermind.Evm;
using Nethermind.StatsAnalyzer.Plugin.Analyzer.Pattern;
using Nethermind.StatsAnalyzer.Plugin.Types;

namespace Nethermind.StatsAnalyzer.Plugin.Tracer.Pattern;

public sealed class PatternStatsAnalyzerTxTracer : StatsAnalyzerTxTracer<Instruction, PatternStat, PatternAnalyzerTxTrace>
{
    private readonly HashSet<Instruction> _ignoreSet;

    public PatternStatsAnalyzerTxTracer(ResettableList<Instruction> buffer, HashSet<Instruction> ignoreSet,
        PatternStatsAnalyzer patternStatsAnalyzer, SortOrder sort, CancellationToken ct) : base(buffer,
        patternStatsAnalyzer, sort, ct)
    {
        _ignoreSet = ignoreSet;
        IsTracingInstructions = true;
    }


    public void AddTxEndMarker()
    {
        if (Skip) return;
        Queue?.Enqueue(NGram.Reset);
    }


    public override PatternAnalyzerTxTrace BuildResult(ulong fromBlock = 0UL, ulong toBlock = 0UL)
    {
        Build();
        PatternAnalyzerTxTrace trace = new();
        trace.InitialBlockNumber = fromBlock;
        trace.CurrentBlockNumber = toBlock;
        trace.Confidence = ((PatternStatsAnalyzer)StatsAnalyzer).Confidence;
        trace.ErrorPerItem = ((PatternStatsAnalyzer)StatsAnalyzer).Error;

        IEnumerable<PatternStat> stats = StatsAnalyzer.Stats(Sort);

        foreach (PatternStat stat in stats)
        {
            PatternAnalyzerTraceEntry entry = new()
            {
                Pattern = stat.Ngram.ToString(),
                Bytes = stat.Ngram.ToBytes(),
                Count = stat.Count
            };
            trace.Entries.Add(entry);
        }

        return trace;
    }


    public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
    {
        if (Skip) return;
        if (!_ignoreSet.Contains(opcode)) Queue?.Enqueue(opcode);
    }
}
