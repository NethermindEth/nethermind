// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using System.Text.Json;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
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
    string? fileName,
    IBlocksConfig blocksConfig) : BlockTracerBase<TxTrace, TxTracer>
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
    private readonly IBlocksConfig _blocksConfig = blocksConfig ?? throw new ArgumentNullException(nameof(blocksConfig));
    private int _pos;
    private ulong _currentBlock;
    private ulong _initialBlock;
    private Task _lastTask = Task.CompletedTask;
    protected TxTracer Tracer = tracer;
    private bool _skipThisBlock;
    private bool _loggedFirstSkip;

    protected abstract void ResetBufferAndTracer();

    public override void EndBlockTrace()
    {
        TxTracer tracer = Tracer;
        ulong initialBlockNumber = _initialBlock;
        ulong currentBlockNumber = _currentBlock;
        bool skip = _skipThisBlock;

        ResetBufferAndTracer();

        if (!skip)
        {
            Enqueue(() =>
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
            });
        }

        base.EndBlockTrace();
    }

    private void Enqueue(Action work)
    {
        if (_fileTracingQueueSize < 1) return;

        _pos = (_pos + 1) % _writeFreq;
        if (_pos != 0) return;

        // Chain via ContinueWith so the runtime starts the task itself rather
        // than leaving it in Created state where Wait/WaitAll would race.
        Task previous = _lastTask;
        Task task = previous.ContinueWith(t =>
        {
            if (t.Exception != null)
                Logger.Error($"Previous task failed: {t.Exception.Flatten()}");
            work();
        }, Ct);
        _lastTask = task;

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
        ulong number = block.Header.Number;

        // Approximates BlockAccessListManager.ParallelExecutionEnabled; missing
        // clauses are subtractive, so this never under-skips (no race), only
        // over-skips a few benign cases (e.g. local block production).
        _skipThisBlock = _blocksConfig.ParallelExecution && block.BlockAccessList is not null;
        Tracer.SetSkip(_skipThisBlock);

        if (_skipThisBlock)
        {
            if (!_loggedFirstSkip && Logger.IsInfo)
            {
                Logger.Info(
                    $"StatsAnalyzer skipping block {number}: parallel BAL execution active. " +
                    "Set Blocks.ParallelExecution=false to record every block on this node " +
                    "(see tools/StatsAnalyzer/EIP-7928-references.md).");
                _loggedFirstSkip = true;
            }
            // Skipped blocks don't advance the recorded range.
            return;
        }

        // 0 is the "unset" sentinel; genesis won't anchor _initialBlock, the
        // first non-genesis block traced does.
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
        ulong initialBlockNumber,
        ulong currentBlockNumber,
        IStatsAnalyzerTxTracer<TxTrace> tracer,
        string fileName,
        IFileSystem fileSystem,
        JsonSerializerOptions serializerOptions,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        TxTrace trace = tracer.BuildResult(initialBlockNumber, currentBlockNumber);

        ct.ThrowIfCancellationRequested();

        using Stream file = fileSystem.File.Open(fileName, FileMode.Create, FileAccess.Write);
        using Utf8JsonWriter jsonWriter = new(file);
        JsonSerializer.Serialize(jsonWriter, trace, serializerOptions);
    }
}
