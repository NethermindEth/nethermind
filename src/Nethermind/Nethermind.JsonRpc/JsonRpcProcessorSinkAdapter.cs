// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc;

internal static class JsonRpcProcessorSinkAdapter
{
    public static async ValueTask ProcessAsync(
        IJsonRpcProcessor processor,
        PipeReader reader,
        JsonRpcContext context,
        IJsonRpcResponseSink sink,
        JsonRpcProcessingOptions options,
        CancellationToken cancellationToken = default)
    {
        await foreach (JsonRpcResult result in processor.ProcessAsync(reader, context).WithCancellation(cancellationToken))
        {
            using (result)
            {
                if (result.IsCollection)
                {
                    await WriteBatchAsync(result.BatchedResponses, sink, cancellationToken);
                }
                else
                {
                    await sink.WriteSingleAsync(result.Response, result.Report.GetValueOrDefault(), cancellationToken);
                }
            }
        }
    }

    private static async ValueTask WriteBatchAsync(
        IJsonRpcBatchResult batchedResponses,
        IJsonRpcResponseSink sink,
        CancellationToken cancellationToken)
    {
        await sink.BeginBatchAsync(cancellationToken);

        JsonRpcBatchResultAsyncEnumerator enumerator = batchedResponses.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (await enumerator.MoveNextAsync())
            {
                JsonRpcResult.Entry entry = enumerator.Current;
                using (entry)
                {
                    await sink.WriteBatchItemAsync(entry.Response, entry.Report, cancellationToken);
                    if (sink.StopRequested)
                    {
                        enumerator.IsStopped = true;
                    }
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        await sink.EndBatchAsync(cancellationToken);
    }
}
