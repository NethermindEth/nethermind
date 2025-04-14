// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.PatternAnalyzer.Plugin.Types;

namespace Nethermind.StatsAnalyzer.Plugin.Tracer;

public abstract class StatsAnalyzerFileTracer<TxTrace, TxTracer> : BlockTracerBase<TxTrace, TxTracer>
    where TxTracer : class, ITxTracer, IStatsAnalyzerTxTracer<TxTrace>

{
    private readonly List<Task> _fileTracingQueue = [];
    private readonly int _fileTracingQueueSize = 1;
    private readonly int _writeFreq = 1;
    protected readonly CancellationToken Ct;
    protected readonly string FileName;
    private readonly IFileSystem _fileSystem;
    protected readonly ILogger Logger;
    private readonly ProcessingMode _processingMode;
    private readonly JsonSerializerOptions _serializerOptions = new();
    protected readonly SortOrder Sort;
    private int _pos;
    private long _currentBlock;
    protected Task CurrentTask = Task.CompletedTask;
    private long _initialBlock;
    private Task _lastTask = Task.CompletedTask;
    protected TxTracer Tracer;

    protected StatsAnalyzerFileTracer(
        TxTracer tracer,
        string defaultFile,
        int processingQueueSize,
        IFileSystem fileSystem,
        ILogger logger,
        int writeFreq,
        ProcessingMode mode,
        SortOrder sort,
        CancellationToken ct,
        string? fileName)
    {
        Tracer = tracer;
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _writeFreq = writeFreq;
        _fileTracingQueueSize = processingQueueSize;
        FileName = fileName ?? defaultFile;
        Logger = logger;
        _processingMode = mode;
        Sort = sort;
        Ct = ct;
    }


    protected abstract void ResetBufferAndTracer();

    public override void EndBlockTrace()
    {
        var tracer = Tracer;
        var initialBlockNumber = _initialBlock;
        var currentBlockNumber = _currentBlock;

        ResetBufferAndTracer();

        Enqueue(new Task(() =>
            {
                Ct.ThrowIfCancellationRequested();
                WriteTrace(
                    initialBlockNumber,
                    currentBlockNumber,
                    tracer,
                    FileName,
                    _fileSystem,
                    _serializerOptions,
                    Ct);
            },
            Ct));

        base.EndBlockTrace();
    }

    private void Enqueue(Task task)
    {
        if (_fileTracingQueueSize < 1) return;

        _pos = (_pos + 1) % _writeFreq;
        if (_pos != 0) return;


        // var task = CurrentTask;
        _lastTask = _lastTask.ContinueWith(t =>
        {
            if (t.Exception != null)
                Logger.Error($"Previous task failed: {t.Exception.Flatten()}");

            task.Start();
        }, Ct);

        _fileTracingQueue.Add(task);

        if (_fileTracingQueue.Count >= _fileTracingQueueSize)
            switch (_processingMode)
            {
                case ProcessingMode.Bulk:
                    Task.WaitAll(_fileTracingQueue.ToArray(), Ct);
                    _fileTracingQueue.Clear();
                    break;
                case ProcessingMode.Sequential:
                    {
                        var firstUnfinishedTask = _fileTracingQueue.FirstOrDefault(t => !t.IsCompleted);
                        if (firstUnfinishedTask != null) firstUnfinishedTask.Wait(Ct);
                        break;
                    }
            }

        CleanUpCompletedTasks();
    }


    private void CleanUpCompletedTasks()
    {
        _fileTracingQueue.RemoveAll(t => t.IsCompleted);
    }


    public TxTracer StartNewTxTrace(Transaction? tx)
    {
        return Tracer;
    }

    public override void StartNewBlockTrace(Block block)
    {
        base.StartNewBlockTrace(block);
        var number = block.Header.Number;
        if (_initialBlock == 0)
            _initialBlock = number;
        _currentBlock = number;
    }


    public void CompleteAllTasks()
    {
        if (Ct.IsCancellationRequested) _fileTracingQueue.Clear();
        Task.WaitAll(_fileTracingQueue.ToArray(), Ct);
    }

    protected override TxTracer OnStart(Transaction? tx)
    {
        return Tracer;
    }

    public override void EndTxTrace()
    {
    }

    protected override TxTrace OnEnd(TxTracer txTracer)
    {
        throw new NotImplementedException();
    }

    private static void WriteTrace(
        long initialBlockNumber,
        long currentBlockNumber,
        IStatsAnalyzerTxTracer<TxTrace> tracer,
        string fileName,
        IFileSystem fileSystem,
        JsonSerializerOptions serializerOptions,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var trace = tracer.BuildResult(initialBlockNumber, currentBlockNumber);

        ct.ThrowIfCancellationRequested();

        File.WriteAllText(fileName, string.Empty);
        using (var file = fileSystem.File.OpenWrite(fileName))
        using (var jsonWriter = new Utf8JsonWriter(file))
        {
            JsonSerializer.Serialize(jsonWriter, trace, serializerOptions);
        }
    }
}
