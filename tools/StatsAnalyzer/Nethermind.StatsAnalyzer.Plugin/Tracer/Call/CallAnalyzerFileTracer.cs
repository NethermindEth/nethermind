// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Resettables;
using Nethermind.Logging;
using Nethermind.PatternAnalyzer.Plugin.Analyzer.Call;
using Nethermind.PatternAnalyzer.Plugin.Types;

namespace Nethermind.StatsAnalyzer.Plugin.Tracer.Call;

public class CallAnalyzerFileTracer : StatsAnalyzerFileTracer<CallAnalyzerTxTrace, CallAnalyzerTxTracer>
{
    private readonly CallStatsAnalyzer _callStatsAnalyzer;
    private DisposableResettableList<Address> _buffer;

    public CallAnalyzerFileTracer(DisposableResettableList<Address> buffer, int processingQueueSize,
        CallStatsAnalyzer callStatsAnalyzer,
        IFileSystem fileSystem, ILogger logger, int writeFreq, ProcessingMode mode,
        SortOrder sort,
        string fileName, CancellationToken ct) : base(new CallAnalyzerTxTracer(buffer, callStatsAnalyzer, sort, ct),
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


    public override void EndBlockTrace()
    {
        var tracer = Tracer;
        var initialBlockNumber = InitialBlock;
        var currentBlockNumber = CurrentBlock;

        _buffer = new DisposableResettableList<Address>();
        Tracer = new CallAnalyzerTxTracer(_buffer, _callStatsAnalyzer, Sort,
            Ct);

        var semaphore = WriteLock;

        CurrentTask = new Task(() =>
        {
            Ct.ThrowIfCancellationRequested();
            WriteTrace(initialBlockNumber, currentBlockNumber, tracer, FileName, FileSystem, SerializerOptions, Ct,
                semaphore);
        }, Ct);

        base.EndBlockTrace();
    }


    private static void WriteTrace(long initialBlockNumber, long currentBlockNumber, CallAnalyzerTxTracer tracer,
        string fileName, IFileSystem fileSystem, JsonSerializerOptions serializerOptions, CancellationToken ct,
        Semaphore semaphore)
    {
        ct.ThrowIfCancellationRequested();

        var trace = tracer.BuildResult();
        trace.InitialBlockNumber = initialBlockNumber;
        trace.CurrentBlockNumber = currentBlockNumber;

        ct.ThrowIfCancellationRequested();

        semaphore.WaitOne();
        try
        {
            File.WriteAllText(fileName, string.Empty);
            using (var file = fileSystem.File.OpenWrite(fileName))
            using (var jsonWriter = new Utf8JsonWriter(file))
            {
                JsonSerializer.Serialize(jsonWriter, trace, serializerOptions);
            }
        }
        finally
        {
            semaphore.Release();
        }
    }
}
