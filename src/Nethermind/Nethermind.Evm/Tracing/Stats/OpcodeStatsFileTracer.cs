
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

namespace Nethermind.Evm.Tracing.OpcodeStats;

public class OpcodeStatsFileTracer : OpcodeStatsTracer
{

    const string DefaultFile = "op_code_stats.json";
    private readonly string _fileName;
    private readonly IFileSystem _fileSystem;
    private readonly JsonSerializerOptions _serializerOptions = new();
    List<Task> _fileTracingQueue = new List<Task>();
    private ILogger _logger;
    int _fileTracingQueueSize = 1;
    private static readonly object _fileLock = new object();

    public OpcodeStatsFileTracer(int processingQueueSize, int bufferSize, StatsAnalyzer statsAnalyzer, IFileSystem fileSystem, ILogger logger, string? fileName) : base(bufferSize, statsAnalyzer)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _fileTracingQueueSize = processingQueueSize;
        _fileName = fileName ?? _fileSystem.Path.Combine(_fileSystem.Path.GetTempPath(), DefaultFile);
        _logger = logger;
        if (_logger.IsInfo) _logger.Info($"OpcodeStats file tracer is set with processing queue size: {_fileTracingQueueSize}, buffer size: {_bufferSize} and will write to file: {_fileName} ");
    }


    public override void EndBlockTrace()
    {
        if (_fileTracingQueueSize < 1) return;

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

        var task = Task.Run(() =>
        {
            try
            {
                base.EndBlockTrace();
                DumpStats();
            }
            catch (Exception ex)
            {
                _logger.Error($"Error in EndBlockTrace task: {ex.Message}");
            }
        });

        _fileTracingQueue.Add(task);

        CleanUpCompletedTasks();
    }

    private void CleanUpCompletedTasks()
    {
        _fileTracingQueue.RemoveAll(t => t.IsCompleted || t.IsFaulted || t.IsCanceled);
    }



    private void DumpStats()
    {
        var trace = BuildResult().First();

        if (_logger.IsInfo) _logger.Info($"Writing stats to file: {_fileName}");

        // Ensure only one task writes to the file at a time
        lock (_fileLock)
        {
            try
            {
                // Clear the file before writing (if needed)
                File.WriteAllText(_fileName, string.Empty);

                // Open the file for writing, using 'using' block to ensure it is closed after writing
                using (var _file = _fileSystem.File.OpenWrite(_fileName))
                using (var jsonWriter = new Utf8JsonWriter(_file))
                {
                    // Serialize the trace and write to the file
                    JsonSerializer.Serialize(jsonWriter, trace, _serializerOptions);
                }
            }
            catch (IOException ex)
            {
                _logger.Error($"Error writing to file {_fileName}: {ex.Message}");
                throw;
            }
        }
    }
    private void _DumpStats()
    {
        if (_logger.IsInfo) _logger.Info($"Building Result.....");
        var trace = BuildResult().First();
        if (_logger.IsInfo) _logger.Info($"Built Result, writing to file: {_fileName}");
        File.WriteAllText(_fileName, String.Empty);
        var _file = _fileSystem.File.OpenWrite(_fileName);
        var jsonWriter = new Utf8JsonWriter(_file);
        JsonSerializer.Serialize(jsonWriter, trace, _serializerOptions);
    }

}
