// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using System.Text.Json;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.StatsAnalyzer.Plugin.Types;

namespace Nethermind.StatsAnalyzer.Plugin.Tracer;

public abstract class StatsAnalyzerFileTracer<TxTrace, TxTracer>(
    TxTracer tracer,
    string defaultFile,
    int processingQueueSize,
    IFileSystem fileSystem,
    ILogger logger,
    int writeFreq,
    ProcessingMode mode,
    SortOrder sort,
    CancellationToken ct,
    string? fileName) : BlockTracerBase<TxTrace, TxTracer>
    where TxTracer : class, ITxTracer, IStatsAnalyzerTxTracer<TxTrace>

{
    private readonly List<Task> _fileTracingQueue = [];
    private readonly int _fileTracingQueueSize = processingQueueSize;
    private readonly int _writeFreq = writeFreq;
    protected readonly CancellationToken Ct = ct;
    protected readonly string FileName = fileName ?? defaultFile;
    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    protected readonly ILogger Logger = logger;
    private readonly ProcessingMode _processingMode = mode;
    private readonly JsonSerializerOptions _serializerOptions = new();
    protected readonly SortOrder Sort = sort;
    private int _pos;
    private long _currentBlock;
    protected Task CurrentTask = Task.CompletedTask;
    private long _initialBlock;
    private Task _lastTask = Task.CompletedTask;
    protected TxTracer Tracer = tracer;

    protected abstract void ResetBufferAndTracer();

    public override void EndBlockTrace()
    {
        TxTracer tracer = Tracer;
        long initialBlockNumber = _initialBlock;
        long currentBlockNumber = _currentBlock;

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
                        Task? firstUnfinishedTask = _fileTracingQueue.FirstOrDefault(t => !t.IsCompleted);
                        if (firstUnfinishedTask != null) firstUnfinishedTask.Wait(Ct);
                        break;
                    }
            }

        CleanUpCompletedTasks();
    }


    private void CleanUpCompletedTasks() => _fileTracingQueue.RemoveAll(t => t.IsCompleted);


    public TxTracer StartNewTxTrace(Transaction? tx) => Tracer;

    public override void StartNewBlockTrace(Block block)
    {
        base.StartNewBlockTrace(block);
        long number = block.Header.Number;
        // _initialBlock == 0 means "unset" rather than "genesis"; on a
        // fresh node this means the genesis block itself does not anchor
        // _initialBlock — the first non-genesis block the tracer sees does.
        // Acceptable for the analyzer's accounting (the first traced block
        // is the start of the trace), but document the off-by-one.
        if (_initialBlock == 0)
            _initialBlock = number;
        _currentBlock = number;
    }


    public void CompleteAllTasks()
    {
        if (Ct.IsCancellationRequested) _fileTracingQueue.Clear();
        Task.WaitAll(_fileTracingQueue.ToArray(), Ct);
    }

    protected override TxTracer OnStart(Transaction? tx) => Tracer;

    public override void EndTxTrace()
    {
    }

    protected override TxTrace OnEnd(TxTracer txTracer) => throw new NotImplementedException();

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

        TxTrace trace = tracer.BuildResult(initialBlockNumber, currentBlockNumber);

        ct.ThrowIfCancellationRequested();

        // FileMode.Create truncates the file in a single open via the injected
        // IFileSystem, so MockFileSystem-backed tests see the truncation.
        using (Stream file = fileSystem.File.Open(fileName, FileMode.Create, FileAccess.Write))
        using (Utf8JsonWriter jsonWriter = new(file))
        {
            JsonSerializer.Serialize(jsonWriter, trace, serializerOptions);
        }
    }
}
