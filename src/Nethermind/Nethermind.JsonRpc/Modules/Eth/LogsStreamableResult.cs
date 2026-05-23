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
    ILogger logger) : IBatchAwareStreamableResult, IEnumerable<FilterLog>, IDisposable
{
    private const long FlushThresholdBytes = 64 * 1024;
    private const long EnvelopeEndReserveBytes = 128;
    private static readonly JsonWriterOptions _itemWriterOptions = new() { SkipValidation = true };

    public ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken) =>
        WriteToAsync(writer, isBatch: false, cancellationToken);

    public async ValueTask WriteToAsync(PipeWriter writer, bool isBatch, CancellationToken cancellationToken)
    {
        using CancellationTokenSource linkedCts =
            CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
        CancellationToken combinedToken = linkedCts.Token;
        long? maxResponseBodySize = GetMaxResponseBodySize(isBatch);

        writer.Write("["u8);

        try
        {
            await WriteLogsAsync(writer, maxResponseBodySize, combinedToken);
        }
        catch (OperationCanceledException) when (combinedToken.IsCancellationRequested)
        {
            if (logger.IsDebug) logger.Debug("eth_getLogs streaming cancelled mid-response; client receives a partial result with the JSON envelope closed.");
        }
        finally
        {
            writer.Write("]"u8);
        }
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

    private async ValueTask WriteLogsAsync(PipeWriter writer, long? maxResponseBodySize, CancellationToken cancellationToken)
    {
        using IEnumerator<FilterLog> enumerator = logs.GetEnumerator();
        ArrayBufferWriter<byte> itemBuffer = new();
        int count = 0;
        int estimatedNextLogBytes = 0;

        while (!ReachedLogLimit(count))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (estimatedNextLogBytes > 0 && ReachedResponseBodyLimit(writer, maxResponseBodySize, estimatedNextLogBytes, count))
            {
                return;
            }

            if (!enumerator.MoveNext())
            {
                return;
            }

            itemBuffer.Clear();
            using (Utf8JsonWriter itemWriter = new(itemBuffer, _itemWriterOptions))
            {
                JsonSerializer.Serialize(itemWriter, enumerator.Current, EthereumJsonSerializer.JsonOptions);
            }

            int logBytes = itemBuffer.WrittenCount;
            if (ReachedResponseBodyLimit(writer, maxResponseBodySize, logBytes, count))
            {
                return;
            }

            if (count > 0)
            {
                writer.Write(","u8);
            }

            writer.Write(itemBuffer.WrittenSpan);
            count++;
            estimatedNextLogBytes = logBytes;

            if (await FlushIfNeededAsync(writer, cancellationToken))
            {
                return;
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

    private static ValueTask<bool> FlushIfNeededAsync(PipeWriter writer, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested ? new ValueTask<bool>(true) :
        writer.CanGetUnflushedBytes && writer.UnflushedBytes < FlushThresholdBytes ? new ValueTask<bool>(false) :
        FlushAsync(writer, cancellationToken);

    private static async ValueTask<bool> FlushAsync(PipeWriter writer, CancellationToken cancellationToken)
    {
        FlushResult flushResult = await writer.FlushAsync(cancellationToken);
        return flushResult.IsCompleted || flushResult.IsCanceled;
    }
}
