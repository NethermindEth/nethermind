// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.TxPool.Profiling;

/// <summary>
/// Append-only JSONL transaction profiling store under the configured Nethermind DB path.
/// </summary>
public sealed class TxProfilingJsonDb : ITxProfilingDb, IDisposable
{
    private const int QueueCapacity = 65_536;
    private const int FlushEveryRecords = 1024;
    private const long MaxCurrentFileBytes = 1024L * 1024 * 1024;
    private const int MaxRotatedFiles = 16;

    private static readonly byte[] NewLine = [(byte)'\n'];
    private static readonly JsonWriterOptions JsonWriterOptions = new() { SkipValidation = true };
    private static readonly TimeSpan RotationFailureRetryDelay = TimeSpan.FromSeconds(30);

    private readonly ILogger _logger;
    private readonly Channel<byte[]>? _records;
    private readonly Task? _writerTask;
    private readonly string _directory;
    private readonly string _fileNameWithoutExtension;
    private readonly string _extension;
    private long _droppedRecords;
    private int _dropWarningLogged;
    private int _writeFailureLogged;
    private int _sizeFailureLogged;

    /// <summary>
    /// Creates a profiler at a specific JSONL file path.
    /// </summary>
    public TxProfilingJsonDb(string filePath, ILogManager logManager)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(logManager);

        _logger = logManager.GetClassLogger<TxProfilingJsonDb>();

        string fullPath = Path.GetFullPath(filePath);
        FilePath = fullPath;
        _directory = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        _fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fullPath);
        _extension = Path.GetExtension(fullPath);

        try
        {
            Directory.CreateDirectory(_directory);
            _records = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
            _writerTask = Task.Run(WriteLoop);

            if (_logger.IsInfo) _logger.Info($"Transaction profiling JSONL db: {fullPath}");
        }
        catch (Exception exception)
        {
            if (_logger.IsError) _logger.Error($"Could not open transaction profiling JSONL db at {filePath}.", exception);
        }
    }

    /// <inheritdoc/>
    public string? FilePath { get; }

    /// <inheritdoc/>
    public long DroppedRecords => Interlocked.Read(ref _droppedRecords);

    /// <inheritdoc/>
    public void RecordHash(
        string eventName,
        Hash256? txHash,
        string? peer = null,
        string? protocol = null,
        string? direction = null,
        string? reason = null,
        TxType? txType = null,
        int? txSize = null)
    {
        if (_records is null)
        {
            return;
        }

        ArrayBufferWriter<byte> buffer = StartRecord(eventName);
        using Utf8JsonWriter writer = new(buffer, JsonWriterOptions);
        WriteCommon(writer, eventName, peer, protocol, direction, reason);
        WriteString(writer, "txHash", txHash?.ToString());
        WriteString(writer, "txType", txType?.ToString());
        WriteNumber(writer, "txSize", txSize);
        EndRecord(writer, buffer);
    }

    /// <inheritdoc/>
    public void RecordTx(
        string eventName,
        Transaction? tx,
        string? peer = null,
        string? protocol = null,
        string? direction = null,
        string? reason = null,
        AcceptTxResult? result = null)
    {
        if (_records is null)
        {
            return;
        }

        ArrayBufferWriter<byte> buffer = StartRecord(eventName);
        using Utf8JsonWriter writer = new(buffer, JsonWriterOptions);
        WriteCommon(writer, eventName, peer, protocol, direction, reason);
        WriteString(writer, "result", result?.ToString());

        if (tx is not null)
        {
            WriteString(writer, "txHash", tx.Hash?.ToString());
            WriteString(writer, "sender", tx.SenderAddress?.ToString());
            WriteString(writer, "nonce", tx.Nonce.ToString());
            WriteString(writer, "txType", tx.Type.ToString());
            writer.WriteBoolean("blob", tx.SupportsBlobs);
            WriteNumber(writer, "txSize", GetTransactionSize(tx));
        }

        EndRecord(writer, buffer);
    }

    /// <inheritdoc/>
    public void RecordResource(
        string eventName,
        string resourceId,
        string? peer = null,
        string? reason = null)
    {
        if (_records is null)
        {
            return;
        }

        ArrayBufferWriter<byte> buffer = StartRecord(eventName);
        using Utf8JsonWriter writer = new(buffer, JsonWriterOptions);
        WriteCommon(writer, eventName, peer, protocol: null, direction: null, reason);
        WriteString(writer, "resourceId", resourceId);
        EndRecord(writer, buffer);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _records?.Writer.TryComplete();

        try
        {
            _writerTask?.GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            if (_logger.IsError) _logger.Error($"Transaction profiling JSONL writer failed for {FilePath}.", exception);
        }

        long droppedRecords = DroppedRecords;
        if (droppedRecords != 0 && _logger.IsWarn)
        {
            _logger.Warn($"Transaction profiling dropped {droppedRecords} records because the writer queue was full.");
        }
    }

    private ArrayBufferWriter<byte> StartRecord(string eventName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        return new ArrayBufferWriter<byte>(512);
    }

    private static void WriteCommon(
        Utf8JsonWriter writer,
        string eventName,
        string? peer,
        string? protocol,
        string? direction,
        string? reason)
    {
        writer.WriteStartObject();
        writer.WriteString("timestampUtc", DateTimeOffset.UtcNow);
        writer.WriteString("event", eventName);
        WriteString(writer, "peer", peer);
        WriteString(writer, "protocol", protocol);
        WriteString(writer, "direction", direction);
        WriteString(writer, "reason", reason);
    }

    private void EndRecord(Utf8JsonWriter writer, ArrayBufferWriter<byte> buffer)
    {
        writer.WriteEndObject();
        writer.Flush();
        EnqueueLine(buffer.WrittenSpan);
    }

    private void EnqueueLine(ReadOnlySpan<byte> line)
    {
        Channel<byte[]>? records = _records;
        if (records is null)
        {
            return;
        }

        if (!records.Writer.TryWrite(line.ToArray()))
        {
            Interlocked.Increment(ref _droppedRecords);
            if (Interlocked.Exchange(ref _dropWarningLogged, 1) == 0 && _logger.IsWarn)
            {
                _logger.Warn($"Transaction profiling queue is full. Dropping records until the writer catches up. Capacity: {QueueCapacity}.");
            }
        }
    }

    private async Task WriteLoop()
    {
        Channel<byte[]>? records = _records;
        if (records is null || FilePath is null)
        {
            return;
        }

        FileStream? stream = null;
        try
        {
            long currentFileBytes = 0;
            int recordsSinceFlush = 0;
            long lastFlushTimestamp = Stopwatch.GetTimestamp();
            long lastRotationFailureTimestamp = 0;

            await foreach (byte[] line in records.Reader.ReadAllAsync())
            {
                try
                {
                    stream ??= OpenCurrentFile(out currentFileBytes);

                    bool shouldRetryRotation = lastRotationFailureTimestamp == 0
                        || Stopwatch.GetElapsedTime(lastRotationFailureTimestamp) >= RotationFailureRetryDelay;
                    if (currentFileBytes + line.Length + NewLine.Length > MaxCurrentFileBytes && shouldRetryRotation)
                    {
                        FileStream streamToRotate = stream;
                        stream = null;
                        bool rotated = await TryRotateCurrentFile(streamToRotate);
                        stream = OpenCurrentFile(out currentFileBytes);
                        if (rotated)
                        {
                            recordsSinceFlush = 0;
                            lastFlushTimestamp = Stopwatch.GetTimestamp();
                            lastRotationFailureTimestamp = 0;
                        }
                        else
                        {
                            lastRotationFailureTimestamp = Stopwatch.GetTimestamp();
                        }
                    }

                    await stream.WriteAsync(line);
                    await stream.WriteAsync(NewLine);
                    currentFileBytes += line.Length + NewLine.Length;
                    recordsSinceFlush++;

                    if (recordsSinceFlush >= FlushEveryRecords || Stopwatch.GetElapsedTime(lastFlushTimestamp).TotalSeconds >= 1)
                    {
                        await stream.FlushAsync();
                        recordsSinceFlush = 0;
                        lastFlushTimestamp = Stopwatch.GetTimestamp();
                    }
                }
                catch (Exception exception)
                {
                    Interlocked.Increment(ref _droppedRecords);
                    LogWriteFailure(exception);

                    if (stream is not null)
                    {
                        await DisposeStream(stream);
                        stream = null;
                    }
                }
            }

            if (stream is not null)
            {
                await stream.FlushAsync();
            }
        }
        catch (Exception exception)
        {
            LogWriteFailure(exception);
        }
        finally
        {
            if (stream is not null)
            {
                await DisposeStream(stream);
            }
        }
    }

    private int? GetTransactionSize(Transaction tx)
    {
        try
        {
            return tx.GetLength(tx.NetworkWrapper is ShardBlobNetworkWrapper);
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _sizeFailureLogged, 1) == 0 && _logger.IsWarn)
            {
                _logger.Warn($"Failed to calculate transaction profiling size for {tx.Hash}: {exception}");
            }

            return null;
        }
    }

    private async ValueTask<bool> TryRotateCurrentFile(FileStream stream)
    {
        try
        {
            await stream.FlushAsync();
        }
        catch (Exception exception)
        {
            LogWriteFailure(exception);
            await DisposeStream(stream);
            return false;
        }

        await DisposeStream(stream);

        try
        {
            RotateCurrentFile();
            return true;
        }
        catch (Exception exception)
        {
            LogWriteFailure(exception);
            return false;
        }
    }

    private async ValueTask DisposeStream(FileStream stream)
    {
        try
        {
            await stream.DisposeAsync();
        }
        catch (Exception exception)
        {
            LogWriteFailure(exception);
        }
    }

    private void LogWriteFailure(Exception exception)
    {
        if (Interlocked.Exchange(ref _writeFailureLogged, 1) == 0 && _logger.IsError)
        {
            _logger.Error($"Failed to write transaction profiling records to {FilePath}.", exception);
        }
    }

    private FileStream OpenCurrentFile(out long currentFileBytes)
    {
        FileInfo fileInfo = new(FilePath!);
        currentFileBytes = fileInfo.Exists ? fileInfo.Length : 0;
        return new FileStream(FilePath!, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 64 * 1024, FileOptions.Asynchronous);
    }

    private void RotateCurrentFile()
    {
        if (FilePath is null || !File.Exists(FilePath))
        {
            return;
        }

        string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        for (int i = 0; ; i++)
        {
            string suffix = i == 0 ? timestamp : $"{timestamp}-{i}";
            string rotatedPath = Path.Combine(_directory, $"{_fileNameWithoutExtension}-{suffix}{_extension}");
            if (File.Exists(rotatedPath))
            {
                continue;
            }

            File.Move(FilePath, rotatedPath);
            if (_logger.IsInfo) _logger.Info($"Rotated transaction profiling JSONL db to {rotatedPath}");
            PruneRotatedFiles();
            return;
        }
    }

    private void PruneRotatedFiles()
    {
        try
        {
            DirectoryInfo directory = new(_directory);
            FileInfo[] rotatedFiles = directory.GetFiles($"{_fileNameWithoutExtension}-*{_extension}");
            if (rotatedFiles.Length <= MaxRotatedFiles)
            {
                return;
            }

            Array.Sort(rotatedFiles, static (left, right) => left.LastWriteTimeUtc.CompareTo(right.LastWriteTimeUtc));
            int filesToDelete = rotatedFiles.Length - MaxRotatedFiles;
            for (int i = 0; i < filesToDelete; i++)
            {
                rotatedFiles[i].Delete();
            }
        }
        catch (Exception exception)
        {
            if (_logger.IsWarn) _logger.Warn($"Failed to prune old transaction profiling JSONL files in {_directory}: {exception}");
        }
    }

    private static void WriteString(Utf8JsonWriter writer, string propertyName, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            writer.WriteString(propertyName, value);
        }
    }

    private static void WriteNumber(Utf8JsonWriter writer, string propertyName, int? value)
    {
        if (value.HasValue)
        {
            writer.WriteNumber(propertyName, value.Value);
        }
    }
}
