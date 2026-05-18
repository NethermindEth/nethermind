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
    private long _currentBlock;
    protected Task CurrentTask = Task.CompletedTask;
    private long _initialBlock;
    private Task _lastTask = Task.CompletedTask;
    protected TxTracer Tracer = tracer;
    // Per-block flag set in StartNewBlockTrace. When true, this block is being
    // processed under parallel-BAL execution and the per-tx tracer must short-
    // circuit; the file write at EndBlockTrace is also suppressed. See
    // tools/StatsAnalyzer/EIP-7928-references.md and BAL-statsanalyzer-plan.md
    // §6c for the gating rationale.
    private bool _skipThisBlock;
    // One-shot log on the first skipped block — avoids per-block log spam.
    private bool _loggedFirstSkip;

    protected abstract void ResetBufferAndTracer();

    public override void EndBlockTrace()
    {
        TxTracer tracer = Tracer;
        long initialBlockNumber = _initialBlock;
        long currentBlockNumber = _currentBlock;
        bool skip = _skipThisBlock;

        ResetBufferAndTracer();

        if (!skip)
        {
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
        }

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

        // Approximate Nethermind's authoritative
        //   BlockAccessListManager.ParallelExecutionEnabled =
        //     Enabled && blocksConfig.ParallelExecution && !_isBuilding && suggestedBlock.BlockAccessList is not null;
        // The two missing pieces (Enabled, !_isBuilding) are subtractive: when
        // false they force sequential exec, so this approximation never
        // under-skips and only over-skips in narrow benign cases (e.g. local
        // block production with a pre-populated BAL body — we lose stats for
        // those blocks, no correctness break).
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
            // Don't anchor _initialBlock/_currentBlock on skipped blocks so the
            // emitted (initialBlockNumber, currentBlockNumber) range reflects only
            // blocks the analyzer actually recorded.
            return;
        }

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
