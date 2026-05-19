// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc;

/// <summary>
/// Bridges the legacy <see cref="JsonRpcResult"/> enumerable processor path to <see cref="IJsonRpcResponseSink"/>.
/// </summary>
public static class JsonRpcProcessorSinkAdapter
{
    /// <summary>
    /// Processes JSON-RPC input with the legacy processor and forwards responses to a sink.
    /// </summary>
    /// <param name="processor">The legacy JSON-RPC processor.</param>
    /// <param name="reader">The input reader containing JSON-RPC payload bytes.</param>
    /// <param name="context">The request context.</param>
    /// <param name="sink">The response sink.</param>
    /// <param name="options">The processing options reserved for the native sink path.</param>
    /// <param name="cancellationToken">The cancellation token for response writes.</param>
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
