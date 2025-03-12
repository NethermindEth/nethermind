// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Resettables;
using Nethermind.Core.Threading;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.PatternAnalyzer.Plugin.Analyzer;

namespace Nethermind.PatternAnalyzer.Plugin.Tracer;

public class PatternAnalyzerFileTracer : BlockTracerBase<PatternAnalyzerTxTrace, PatternAnalyzerTxTracer>
{
    private const string DefaultFile = "op_code_stats.json";
    private static readonly Lock FileLock = new();
    private readonly int _bufferSize;
    private readonly CancellationToken _ct;
    private readonly string _fileName;
    private readonly IFileSystem _fileSystem;
    private readonly List<Task> _fileTracingQueue = new();
    private readonly int _fileTracingQueueSize = 1;
    private readonly HashSet<Instruction> _ignore;
    private readonly ILogger _logger;
    private readonly McsLock _processingLock = new();
    private readonly JsonSerializerOptions _serializerOptions = new();
    private readonly SortOrder _sort;
    private readonly StatsAnalyzer _statsAnalyzer;
    private readonly int _writeFreq = 1;
    private DisposableResettableList<Instruction> _buffer = new();
    private long _currentBlock;
    private long _initialBlock;
    private int _pos;
    private PatternAnalyzerTxTracer _tracer;


    public PatternAnalyzerFileTracer(int processingQueueSize, int bufferSize, StatsAnalyzer statsAnalyzer,
        HashSet<Instruction> ignore, IFileSystem fileSystem, ILogger logger, int writeFreq, SortOrder sort,
        string fileName, CancellationToken ct)
    {
        _bufferSize = bufferSize;
        _statsAnalyzer = statsAnalyzer;
        _ignore = ignore;
        _tracer = new PatternAnalyzerTxTracer(_buffer, _ignore, _bufferSize, _processingLock, _statsAnalyzer, sort, ct);
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _writeFreq = writeFreq;
        _fileTracingQueueSize = processingQueueSize;
        _fileName = fileName;
        _logger = logger;
        _sort = sort;
        _ct = ct;
        if (_logger.IsInfo)
            _logger.Info(
                $"OpcodeStats file tracer is set with processing queue size: {_fileTracingQueueSize}, buffer size: {_bufferSize} and will write to file: {_fileName} ");
    }


    public override void EndBlockTrace()
    {
        if (_fileTracingQueueSize < 1) return;

        _pos = (_pos + 1) % _writeFreq;

        if (_pos != 0) return;

        if (_fileTracingQueue.Count >= _fileTracingQueueSize && _fileTracingQueue.Count > 0)
            try
            {
                _fileTracingQueue[0].ContinueWith(t =>
                {
                    if (t.IsFaulted) _logger.Error($"Task failed: {t.Exception}");
                }, _ct).Wait(_ct);
            }
            catch (AggregateException ex)
            {
                _logger.Error($"Error while waiting for task: {ex}");
            }

        var tracer = _tracer;
        var initialBlockNumber = _initialBlock;
        var currentBlockNumber = _currentBlock;

        _buffer = new DisposableResettableList<Instruction>();
        _tracer = new PatternAnalyzerTxTracer(_buffer, _ignore, _bufferSize, _processingLock, _statsAnalyzer, _sort,
            _ct);

        var task = Task.Run(() =>
        {
            lock (FileLock)
            {
                try
                {
                    _ct.ThrowIfCancellationRequested();
                    var processingLock = _processingLock.Acquire();
                    WriteTrace(initialBlockNumber, currentBlockNumber, tracer, _fileName, _fileSystem,
                        _serializerOptions, _ct);
                    processingLock.Dispose();
                }
                catch (IOException ex)
                {
                    _logger.Error($"Error writing to file {_fileName}: {ex.Message}");
                    throw;
                }
                catch (OperationCanceledException)
                {
                }
            }
        }, _ct);

        _fileTracingQueue.Add(task);

        CleanUpCompletedTasks();
    }

    private void CleanUpCompletedTasks()
    {
        _fileTracingQueue.RemoveAll(t => t.IsCompleted || t.IsFaulted || t.IsCanceled);
    }

    public override void EndTxTrace()
    {
        _tracer.AddTxEndMarker();
    }

    private static void WriteTrace(long initialBlockNumber, long currentBlockNumber, PatternAnalyzerTxTracer tracer,
        string fileName, IFileSystem fileSystem, JsonSerializerOptions serializerOptions, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var trace = tracer.BuildResult();
        trace.InitialBlockNumber = initialBlockNumber;
        trace.CurrentBlockNumber = currentBlockNumber;

        ct.ThrowIfCancellationRequested();

        File.WriteAllText(fileName, string.Empty);

        // Open the file for writing, using 'using' block to ensure it is closed after writing
        using (var file = fileSystem.File.OpenWrite(fileName))
        using (var jsonWriter = new Utf8JsonWriter(file))
        {
            JsonSerializer.Serialize(jsonWriter, trace, serializerOptions);
        }
    }

    public PatternAnalyzerTxTracer StartNewTxTrace(Transaction? tx)
    {
        return _tracer;
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
        Task.WaitAll(_fileTracingQueue.ToArray(), _ct);
    }

    protected override PatternAnalyzerTxTracer OnStart(Transaction? tx)
    {
        return _tracer;
    }


    protected override PatternAnalyzerTxTrace OnEnd(PatternAnalyzerTxTracer txTracer)
    {
        throw new NotImplementedException();
    }
}
