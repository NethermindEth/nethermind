// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Nethermind.Core;
using Nethermind.Core.Resettables;
using Nethermind.Logging;
using Nethermind.PatternAnalyzer.Plugin.Analyzer.Call;
using Nethermind.PatternAnalyzer.Plugin.Types;

namespace Nethermind.StatsAnalyzer.Plugin.Tracer.Call;

public class CallAnalyzerFileTracer : StatsAnalyzerFileTracer<CallAnalyzerTxTrace, CallStatsAnalyzerTxTracer>
{
    private readonly CallStatsAnalyzer _callStatsAnalyzer;
    private ResettableList<Address> _buffer;

    public CallAnalyzerFileTracer(ResettableList<Address> buffer, int processingQueueSize,
        CallStatsAnalyzer callStatsAnalyzer,
        IFileSystem fileSystem, ILogger logger, int writeFreq, ProcessingMode mode,
        SortOrder sort,
        string fileName, CancellationToken ct) : base(
        new CallStatsAnalyzerTxTracer(buffer, callStatsAnalyzer, sort, ct),
        "call-analyzer.json",
        processingQueueSize, fileSystem, logger, writeFreq, mode,
        sort,
        fileName, ct)

    {
        _callStatsAnalyzer = callStatsAnalyzer;
        _buffer = buffer;
        if (Logger.IsInfo)
            Logger.Info(
                $"Call Analyzer file tracer  will write to stats to file: {FileName} ");
    }


    public override void ResetBufferAndTracer()
    {
        _buffer = new ResettableList<Address>(_buffer.Count);
        Tracer = new CallStatsAnalyzerTxTracer(_buffer, _callStatsAnalyzer, Sort,
            Ct);
    }
}
