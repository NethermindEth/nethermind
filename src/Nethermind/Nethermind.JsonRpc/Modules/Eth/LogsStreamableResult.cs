// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Facade.Filters;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Modules.Eth;

internal sealed class LogsStreamableResult(
    IEnumerable<FilterLog> logs,
    int maxLogsPerResponse,
    bool enforceMaxLogs,
    long? maxLogsResponseBodySize,
    long? maxBatchResponseBodySize,
    CancellationTokenSource timeoutCts,
    ILogger logger) : IBatchAwareStreamableResultWithStatus, IEnumerable<FilterLog>, IDisposable
{
    private const long EnvelopeEndReserveBytes = 128;
    private static readonly JsonWriterOptions _itemWriterOptions = new() { SkipValidation = true };

    public async ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken) =>
        await WriteToWithStatusAsync(writer, isBatch: false, cancellationToken);

    public async ValueTask WriteToAsync(PipeWriter writer, bool isBatch, CancellationToken cancellationToken) =>
        await WriteToWithStatusAsync(writer, isBatch, cancellationToken);

    public ValueTask<StreamableResultStatus> WriteToWithStatusAsync(PipeWriter writer, CancellationToken cancellationToken) =>
        WriteToWithStatusAsync(writer, isBatch: false, cancellationToken);

    public async ValueTask<StreamableResultStatus> WriteToWithStatusAsync(PipeWriter writer, bool isBatch, CancellationToken cancellationToken)
    {
        using CancellationTokenSource linkedCts =
            CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
        CancellationToken combinedToken = linkedCts.Token;
        long? maxResponseBodySize = GetMaxResponseBodySize(isBatch);
        StreamableResultStatus status;

        writer.Write("["u8);

        try
        {
            status = await WriteLogsAsync(writer, maxResponseBodySize, combinedToken);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            status = StreamableResultStatus.Timeout;
            if (logger.IsDebug) logger.Debug("eth_getLogs streaming timed out mid-response; client receives a partial result with the JSON envelope closed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            status = StreamableResultStatus.Cancelled;
            if (logger.IsDebug) logger.Debug("eth_getLogs streaming cancelled mid-response; client receives a partial result with the JSON envelope closed.");
        }
        finally
        {
            writer.Write("]"u8);
        }

        return status;
    }

    public IEnumerator<FilterLog> GetEnumerator()
    {
        int count = 0;
        foreach (FilterLog log in logs)
        {
            if (ReachedLogLimit(count))
            {
                yield break;
            }

            count++;
            yield return log;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose() => timeoutCts.Dispose();

    private async ValueTask<StreamableResultStatus> WriteLogsAsync(PipeWriter writer, long? maxResponseBodySize, CancellationToken cancellationToken)
    {
        using IEnumerator<FilterLog> enumerator = logs.GetEnumerator();
        ArrayBufferWriter<byte> itemBuffer = new();
        int count = 0;
        int estimatedNextLogBytes = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ReachedLogLimit(count))
            {
                return StreamableResultStatus.Truncated;
            }

            if (estimatedNextLogBytes > 0 && ReachedResponseBodyLimit(writer, maxResponseBodySize, estimatedNextLogBytes, count))
            {
                return StreamableResultStatus.Truncated;
            }

            StreamableResultStatus? terminalStatus = TryBufferNextLog(enumerator, itemBuffer);
            if (terminalStatus is not null)
            {
                return terminalStatus.GetValueOrDefault();
            }

            int logBytes = itemBuffer.WrittenCount;
            if (ReachedResponseBodyLimit(writer, maxResponseBodySize, logBytes, count))
            {
                return StreamableResultStatus.Truncated;
            }

            if (count > 0)
            {
                writer.Write(","u8);
            }

            writer.Write(itemBuffer.WrittenSpan);
            count++;
            estimatedNextLogBytes = logBytes;

            if (await StreamableResultWriter.FlushIfNeededAsync(writer, cancellationToken))
            {
                return StreamableResultStatus.Cancelled;
            }
        }

        StreamableResultStatus? TryBufferNextLog(IEnumerator<FilterLog> enumerator, ArrayBufferWriter<byte> itemBuffer)
        {
            try
            {
                if (!enumerator.MoveNext())
                {
                    return StreamableResultStatus.Complete;
                }

                itemBuffer.Clear();
                using Utf8JsonWriter itemWriter = new(itemBuffer, _itemWriterOptions);
                JsonSerializer.Serialize(itemWriter, enumerator.Current, EthereumJsonSerializer.JsonOptions);
                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (logger.IsWarn) logger.Warn($"eth_getLogs streaming failed mid-response: {ex}");
                return StreamableResultStatus.Failed;
            }
        }
    }

    private bool ReachedLogLimit(int count) =>
        enforceMaxLogs && count >= maxLogsPerResponse;

    private long? GetMaxResponseBodySize(bool isBatch) =>
        isBatch && maxBatchResponseBodySize is not null
            ? maxBatchResponseBodySize
            : maxLogsResponseBodySize ?? maxBatchResponseBodySize;

    private static bool ReachedResponseBodyLimit(PipeWriter writer, long? maxResponseBodySize, int logBytes, int count) =>
        maxResponseBodySize is >= 0
        && writer is CountingWriter countingWriter
        && countingWriter.WrittenCount + (count > 0 ? 1 : 0) + logBytes + EnvelopeEndReserveBytes > maxResponseBodySize.Value;

}
