// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc;

/// <summary>Provides low-allocation JSON streaming helpers for RPC results.</summary>
public static class StreamableResultWriter
{
    private const long FlushThresholdBytes = 16 * 1024;

    /// <summary>Writes a JSON array by delegating each item to a struct writer.</summary>
    /// <param name="writer">The destination writer.</param>
    /// <param name="count">The number of array items to write.</param>
    /// <param name="itemWriter">The item writer.</param>
    /// <param name="cancellationToken">The cancellation token used when flushing.</param>
    /// <typeparam name="TItemWriter">The struct writer type.</typeparam>
    public static async ValueTask WriteArrayAsync<TItemWriter>(
        PipeWriter writer,
        int count,
        TItemWriter itemWriter,
        CancellationToken cancellationToken)
        where TItemWriter : struct, IJsonArrayItemWriter
    {
        writer.Write("["u8);

        for (int i = 0; i < count; i++)
        {
            if (i > 0) writer.Write(","u8);

            itemWriter.WriteItem(writer, i);

            if (await FlushIfNeededAsync(writer, cancellationToken))
            {
                writer.Write("]"u8);
                return;
            }
        }

        writer.Write("]"u8);
    }

    /// <summary>Flushes the writer when the buffered payload reaches the streaming threshold.</summary>
    /// <param name="writer">The destination writer.</param>
    /// <param name="cancellationToken">The cancellation token used when flushing.</param>
    /// <returns><see langword="true"/> when the destination is complete or the flush operation is canceled by the writer.</returns>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled.</exception>
    public static ValueTask<bool> FlushIfNeededAsync(PipeWriter writer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return writer.CanGetUnflushedBytes && writer.UnflushedBytes < FlushThresholdBytes
            ? new ValueTask<bool>(false)
            : FlushAsync(writer, cancellationToken);
    }

    private static async ValueTask<bool> FlushAsync(PipeWriter writer, CancellationToken cancellationToken)
    {
        FlushResult flushResult = await writer.FlushAsync(cancellationToken);
        return flushResult.IsCompleted || flushResult.IsCanceled;
    }
}

/// <summary>Writes an indexed JSON array item into a <see cref="PipeWriter"/>.</summary>
public interface IJsonArrayItemWriter
{
    /// <summary>Writes the array item at <paramref name="index"/>.</summary>
    /// <param name="writer">The destination writer.</param>
    /// <param name="index">The item index.</param>
    void WriteItem(PipeWriter writer, int index);
}
