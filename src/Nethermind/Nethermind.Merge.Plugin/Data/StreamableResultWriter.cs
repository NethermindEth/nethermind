// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Merge.Plugin.Data;

internal static class StreamableResultWriter
{
    private const long FlushThresholdBytes = 64 * 1024;

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
                return;
            }
        }

        writer.Write("]"u8);
    }

    public static ValueTask<bool> FlushIfNeededAsync(PipeWriter writer, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new ValueTask<bool>(true);
        }

        if (writer.CanGetUnflushedBytes && writer.UnflushedBytes < FlushThresholdBytes)
        {
            return new ValueTask<bool>(false);
        }

        return FlushAsync(writer, cancellationToken);
    }

    private static async ValueTask<bool> FlushAsync(PipeWriter writer, CancellationToken cancellationToken)
    {
        FlushResult flushResult = await writer.FlushAsync(cancellationToken);
        return flushResult.IsCompleted || flushResult.IsCanceled;
    }
}

internal interface IJsonArrayItemWriter
{
    void WriteItem(PipeWriter writer, int index);
}
