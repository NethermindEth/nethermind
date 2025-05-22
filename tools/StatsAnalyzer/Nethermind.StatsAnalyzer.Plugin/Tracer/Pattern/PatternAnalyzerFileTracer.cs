// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Nethermind.Core.Resettables;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.PatternAnalyzer.Plugin.Analyzer.Pattern;
using Nethermind.PatternAnalyzer.Plugin.Types;

namespace Nethermind.StatsAnalyzer.Plugin.Tracer.Pattern;

public class PatternAnalyzerFileTracer : StatsAnalyzerFileTracer<PatternAnalyzerTxTrace, PatternStatsAnalyzerTxTracer>
{
    private readonly HashSet<Instruction> _ignore;
    private readonly PatternStatsAnalyzer _patternStatsAnalyzer;

    private ResettableList<Instruction> _buffer = new();

    public PatternAnalyzerFileTracer(
        ResettableList<Instruction> buffer,
        int processingQueueSize,
        int bufferSize,
        PatternStatsAnalyzer patternStatsAnalyzer,
        HashSet<Instruction> ignore,
        IFileSystem fileSystem,
        ILogger logger,
        int writeFreq,
        ProcessingMode mode,
        SortOrder sort,
        string fileName,
        CancellationToken ct) : base(
        new PatternStatsAnalyzerTxTracer(buffer, ignore, patternStatsAnalyzer, sort, ct),
        "pattern-analyzer.json",
        processingQueueSize,
        fileSystem,
        logger,
        writeFreq,
        mode,
        sort,
        ct, fileName)
    {
        _buffer = buffer;

        _patternStatsAnalyzer = patternStatsAnalyzer;
        _ignore = ignore;
        if (Logger.IsInfo)
            Logger.Info(
                $"PatternAnalyzer file tracer is set with processing queue size: {processingQueueSize} and will write to file: {FileName} every {writeFreq} blocks ");
    }


    protected override void ResetBufferAndTracer()
    {
        _buffer = new ResettableList<Instruction>(_buffer.Count);
        Tracer = new PatternStatsAnalyzerTxTracer(_buffer, _ignore, _patternStatsAnalyzer, Sort,
            Ct);
    }

    public override void EndTxTrace()
    {
        var tracer = (PatternStatsAnalyzerTxTracer)Tracer;
        tracer.AddTxEndMarker();
    }
}
