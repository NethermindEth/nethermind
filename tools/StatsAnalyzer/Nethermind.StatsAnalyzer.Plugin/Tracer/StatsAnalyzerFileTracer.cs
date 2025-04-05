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
    private readonly List<Task> _fileTracingQueue = new();
    private readonly int _fileTracingQueueSize = 1;
    private readonly int _writeFreq = 1;
    protected readonly CancellationToken Ct;
    protected readonly string FileName;
    protected readonly IFileSystem FileSystem;
    protected readonly ILogger Logger;
    protected readonly ProcessingMode ProcessingMode;
    protected readonly JsonSerializerOptions SerializerOptions = new();
    protected readonly SortOrder Sort;
    protected readonly Semaphore WriteLock = new(1, 1);
    private int _pos;
    protected long CurrentBlock;
    protected Task CurrentTask = Task.CompletedTask;
    protected long InitialBlock;
    protected Task LastTask = Task.CompletedTask;
    protected TxTracer Tracer;

    protected StatsAnalyzerFileTracer(TxTracer tracer, string defaultFile, int processingQueueSize,
        IFileSystem fileSystem,
        ILogger logger, int writeFreq, ProcessingMode mode,
        SortOrder sort,
        string fileName, CancellationToken ct)
    {
        Tracer = tracer;
        FileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _writeFreq = writeFreq;
        _fileTracingQueueSize = processingQueueSize;
        FileName = fileName;
        Logger = logger;
        ProcessingMode = mode;
        Sort = sort;
        Ct = ct;
    }


    public abstract void ResetBufferAndTracer();

    public override void EndBlockTrace()
    {
        var tracer = Tracer;
        var initialBlockNumber = InitialBlock;
        var currentBlockNumber = CurrentBlock;

        ResetBufferAndTracer();

        var semaphore = WriteLock;

        Enqueue(new Task(() =>
        {
            Ct.ThrowIfCancellationRequested();
            WriteTrace(initialBlockNumber, currentBlockNumber, tracer, FileName, FileSystem, SerializerOptions, Ct,
                semaphore);
        }, Ct));

        base.EndBlockTrace();
    }
    public void Enqueue(Task task)
    {
        if (_fileTracingQueueSize < 1) return;

        _pos = (_pos + 1) % _writeFreq;
        if (_pos != 0) return;


       // var task = CurrentTask;
        LastTask = LastTask.ContinueWith(t =>
        {
            if (t.Exception != null)
                Logger.Error($"Previous task failed: {t.Exception.Flatten()}");

            task.Start();
        }, Ct);

        _fileTracingQueue.Add(task);

        if (_fileTracingQueue.Count >= _fileTracingQueueSize)
            switch (ProcessingMode)
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
        if (InitialBlock == 0)
            InitialBlock = number;
        CurrentBlock = number;
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

    protected static void WriteTrace(long initialBlockNumber, long currentBlockNumber, IStatsAnalyzerTxTracer<TxTrace> tracer,
        string fileName, IFileSystem fileSystem, JsonSerializerOptions serializerOptions, CancellationToken ct,
        Semaphore semaphore)
    {
        ct.ThrowIfCancellationRequested();

        var trace = tracer.BuildResult(initialBlockNumber, currentBlockNumber);

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
