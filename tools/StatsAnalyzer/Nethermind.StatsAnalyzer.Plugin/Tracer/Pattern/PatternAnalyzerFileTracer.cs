// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using System.Text.Json;
using Nethermind.Core.Resettables;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.PatternAnalyzer.Plugin.Analyzer.Pattern;
using Nethermind.PatternAnalyzer.Plugin.Types;

namespace Nethermind.StatsAnalyzer.Plugin.Tracer.Pattern;

public class PatternAnalyzerFileTracer : StatsAnalyzerFileTracer<PatternAnalyzerTxTrace, PatternAnalyzerTxTracer>
{
    private readonly HashSet<Instruction> _ignore;
    private readonly PatternStatsAnalyzer _patternStatsAnalyzer;

    private ResettableList<Instruction> _buffer = new();
    //private PatternAnalyzerTxTracer _tracer;

    public PatternAnalyzerFileTracer(ResettableList<Instruction> buffer, int processingQueueSize,
        int bufferSize, PatternStatsAnalyzer patternStatsAnalyzer,
        HashSet<Instruction> ignore, IFileSystem fileSystem, ILogger logger, int writeFreq, ProcessingMode mode,
        SortOrder sort,
        string fileName, CancellationToken ct) : base(
        new PatternAnalyzerTxTracer(buffer, ignore, patternStatsAnalyzer, sort, ct), "pattern-analyzer.json",
        processingQueueSize, fileSystem, logger, writeFreq, mode,
        sort,
        fileName, ct)
    {
        _buffer = buffer;

        _patternStatsAnalyzer = patternStatsAnalyzer;
        _ignore = ignore;
        // _tracer = new PatternAnalyzerTxTracer(_buffer, _ignore, _bufferSize,  _patternStatsAnalyzer, sort, ct);
        if (Logger.IsInfo)
            Logger.Info(
                $"PatternAnalyzer file tracer is set with processing queue size: {processingQueueSize} and will write to file: {FileName} every {writeFreq} blocks ");
    }


    public override void EndBlockTrace()
    {
        var tracer = Tracer;
        var initialBlockNumber = InitialBlock;
        var currentBlockNumber = CurrentBlock;

        _buffer = new ResettableList<Instruction>();
        Tracer = new PatternAnalyzerTxTracer(_buffer, _ignore, _patternStatsAnalyzer, Sort,
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


    public override void EndTxTrace()
    {
        var tracer = (PatternAnalyzerTxTracer)Tracer;
        tracer.AddTxEndMarker();
    }

    private static void WriteTrace(long initialBlockNumber, long currentBlockNumber, PatternAnalyzerTxTracer tracer,
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
