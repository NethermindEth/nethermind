
// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nethermind.Core.Crypto;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using System.IO.Abstractions;
using Nethermind.Evm.CodeAnalysis.StatsAnalyzer;
using Nethermind.Core.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Core.Resettables;

namespace Nethermind.Evm.Tracing.OpcodeStats;

public class OpcodeStatsFileTracer : BlockTracerBase<OpcodeStatsTxTrace, OpcodeStatsTxTracer>
{


    long _initialBlock = 0;
    long _currentBlock = 0;
    private OpcodeStatsTxTracer _tracer;
    protected int _bufferSize;
    private StatsAnalyzer _statsAnalyzer;
    private McsLock _processingLock = new();
    private HashSet<Instruction> _ignore;
    private static readonly object _lock = new object();

    const string DefaultFile = "op_code_stats.json";
    private readonly string _fileName;
    private readonly IFileSystem _fileSystem;
    private readonly JsonSerializerOptions _serializerOptions = new();
    List<Task> _fileTracingQueue = new List<Task>();
    private ILogger _logger;
    int _fileTracingQueueSize = 1;
    private static readonly object _fileLock = new object();
    private int _writeFreq = 1;
    private int _pos = 0;
    private DisposableResettableList<Instruction> _buffer = new DisposableResettableList<Instruction>();


    public OpcodeStatsFileTracer(int processingQueueSize, int bufferSize, StatsAnalyzer statsAnalyzer, HashSet<Instruction> ignore, IFileSystem fileSystem, ILogger logger, int writeFreq, string? fileName) : base()
    {

        _bufferSize = bufferSize;
        _statsAnalyzer = statsAnalyzer;
        _ignore = ignore;

        _tracer = new OpcodeStatsTxTracer(_buffer, _ignore, _bufferSize, _processingLock, _statsAnalyzer);
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _writeFreq = writeFreq;
        _fileTracingQueueSize = processingQueueSize;
        _fileName = fileName ?? _fileSystem.Path.Combine(_fileSystem.Path.GetTempPath(), DefaultFile);
        _logger = logger;
        if (_logger.IsInfo) _logger.Info($"OpcodeStats file tracer is set with processing queue size: {_fileTracingQueueSize}, buffer size: {_bufferSize} and will write to file: {_fileName} ");
    }


    public override void EndBlockTrace()
    {

        if (_fileTracingQueueSize < 1) return;

        _pos = (_pos + 1) % _writeFreq;

        if (_pos != 0) return;

        if ((_fileTracingQueue.Count >= _fileTracingQueueSize) && _fileTracingQueue.Count > 0)
        {
            try
            {
                _fileTracingQueue[0].ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        _logger.Error($"Task failed: {t.Exception}");
                    }
                }).Wait();
            }
            catch (AggregateException ex)
            {
                _logger.Error($"Error while waiting for task: {ex}");
            }
        }

        var tracer = _tracer;
        var initialBlockNumber = _initialBlock;
        var currentBlockNumber = _currentBlock;

        _buffer = new DisposableResettableList<Instruction>();
        _tracer = new OpcodeStatsTxTracer(_buffer, _ignore, _bufferSize, _processingLock, _statsAnalyzer);

        var task = Task.Run(() =>
        {

                lock (_fileLock)
                {
                    try
                    {
                        var processingLock = _processingLock.Acquire();
                        WriteTrace(initialBlockNumber, currentBlockNumber, tracer, _fileName, _fileSystem, _serializerOptions);
                        processingLock.Dispose();
                    }
                    catch (IOException ex)
                    {
                        _logger.Error($"Error writing to file {_fileName}: {ex.Message}");
                        throw;
                    }
                }
        });

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

    public static void WriteTrace(long initialBlockNumber, long currentBlockNumber, OpcodeStatsTxTracer tracer, string fileName, IFileSystem fileSystem, JsonSerializerOptions serializerOptions)
    {
        OpcodeStatsTxTrace trace = tracer.BuildResult();
        trace.InitialBlockNumber = initialBlockNumber;
        trace.CurrentBlockNumber = currentBlockNumber;

        File.WriteAllText(fileName, string.Empty);

        // Open the file for writing, using 'using' block to ensure it is closed after writing
        using (var _file = fileSystem.File.OpenWrite(fileName))
        using (var jsonWriter = new Utf8JsonWriter(_file))
        {
            JsonSerializer.Serialize(jsonWriter, trace, serializerOptions);
        }
    }

    public OpcodeStatsTxTracer StartNewTxTrace(Transaction? tx) => _tracer;

    public override void StartNewBlockTrace(Block block)
    {
            base.StartNewBlockTrace(block);
            var number = block.Header.Number;
            if (_initialBlock == 0)
                _initialBlock = number;
            _currentBlock = number;

    }


    protected override OpcodeStatsTxTracer OnStart(Transaction? tx)
    {
        return _tracer;
    }


    protected override OpcodeStatsTxTrace OnEnd(OpcodeStatsTxTracer txTracer)
    {
        throw new NotImplementedException();
    }

}
