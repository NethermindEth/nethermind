// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Resettables;
using Nethermind.Evm;
using Nethermind.PatternAnalyzer.Plugin.Analyzer.Pattern;
using Nethermind.PatternAnalyzer.Plugin.Types;

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
        Queue?.Enqueue(NGram.Reset);
    }


    public override PatternAnalyzerTxTrace BuildResult(long fromBlock = 0, long toBlock = 0)
    {
        Build();
        PatternAnalyzerTxTrace trace = new();
        trace.InitialBlockNumber = fromBlock;
        trace.CurrentBlockNumber = toBlock;
        trace.Confidence = ((PatternStatsAnalyzer)StatsAnalyzer).Confidence;
        trace.ErrorPerItem = ((PatternStatsAnalyzer)StatsAnalyzer).Error;

        var stats = StatsAnalyzer.Stats(Sort);

        foreach (var stat in stats)
        {
            var entry = new PatternAnalyzerTraceEntry
            {
                Pattern = stat.Ngram.ToString(),
                Bytes = stat.Ngram.ToBytes(),
                Count = stat.Count
            };
            trace.Entries.Add(entry);
        }

        return trace;
    }


    public override void StartOperation(int pc, Instruction opcode, long gas, in ExecutionEnvironment env)
    {
        if (!_ignoreSet.Contains(opcode)) Queue?.Enqueue(opcode);
    }
}
