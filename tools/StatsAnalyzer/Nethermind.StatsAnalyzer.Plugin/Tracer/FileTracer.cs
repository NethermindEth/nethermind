// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.PatternAnalyzer.Plugin.Types;

namespace Nethermind.StatsAnalyzer.Plugin.Tracer;

public class StatsAnalyzerFileTracer<TXTrace, TXTracer> : BlockTracerBase<TXTrace, TXTracer>
    where TXTracer : class, ITxTracer

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
    protected TXTracer Tracer;

    protected StatsAnalyzerFileTracer(TXTracer tracer, string defaultFile, int processingQueueSize,
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


    public override void EndBlockTrace()
    {
        if (_fileTracingQueueSize < 1) return;

        _pos = (_pos + 1) % _writeFreq;
        if (_pos != 0) return;


        var task = CurrentTask;
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


    public TXTracer StartNewTxTrace(Transaction? tx)
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

    protected override TXTracer OnStart(Transaction? tx)
    {
        return Tracer;
    }

    public override void EndTxTrace()
    {
    }

    protected override TXTrace OnEnd(TXTracer txTracer)
    {
        throw new NotImplementedException();
    }
}
