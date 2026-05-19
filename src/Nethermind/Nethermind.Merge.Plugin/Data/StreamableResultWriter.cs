// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Merge.Plugin.Data;

internal static class StreamableResultWriter
{
    private const long FlushThresholdBytes = 64 * 1024;

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
