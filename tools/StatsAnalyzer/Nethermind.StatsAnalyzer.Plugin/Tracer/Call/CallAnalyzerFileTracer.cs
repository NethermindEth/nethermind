// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Resettables;
using Nethermind.Logging;
using Nethermind.StatsAnalyzer.Plugin.Analyzer.Call;
using Nethermind.StatsAnalyzer.Plugin.Types;

namespace Nethermind.StatsAnalyzer.Plugin.Tracer.Call;

public class CallAnalyzerFileTracer : StatsAnalyzerFileTracer<CallAnalyzerTxTrace, CallStatsAnalyzerTxTracer>
{
    private readonly CallStatsAnalyzer _callStatsAnalyzer;
    private ResettableList<Address> _buffer;

    public CallAnalyzerFileTracer(
        ResettableList<Address> buffer,
        int processingQueueSize,
        CallStatsAnalyzer callStatsAnalyzer,
        IFileSystem fileSystem,
        ILogger logger,
        int writeFreq,
        ProcessingMode mode,
        SortOrder sort,
        string fileName,
        CancellationToken ct,
        IBlocksConfig blocksConfig) : base(
        new CallStatsAnalyzerTxTracer(buffer, callStatsAnalyzer, sort, ct),
        "call-analyzer.json",
        processingQueueSize,
        fileSystem,
        logger,
        writeFreq,
        mode,
        sort,
        ct, fileName, blocksConfig)

    {
        _callStatsAnalyzer = callStatsAnalyzer;
        _buffer = buffer;
        if (Logger.IsInfo)
            Logger.Info(
                $"Call Analyzer file tracer will write stats to file: {FileName} ");
    }


    protected override void ResetBufferAndTracer()
    {
        _buffer = new ResettableList<Address>(_buffer.Count);
        Tracer = new CallStatsAnalyzerTxTracer(_buffer, _callStatsAnalyzer, Sort,
            Ct);
    }
}
