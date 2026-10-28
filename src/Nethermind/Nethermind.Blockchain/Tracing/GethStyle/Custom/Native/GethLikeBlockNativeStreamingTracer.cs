// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Json;

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.Native;

/// <summary>
/// Writes each transaction's native tracer result to the response as its <c>{"result", "txHash"}</c> entry once the
/// transaction ends, releasing the trace as soon as it is written rather than holding the block's traces.
/// </summary>
internal sealed class GethLikeBlockNativeStreamingTracer(
    NativeTracerFactory txTracerFactory,
    Utf8JsonWriter writer,
    PipeWriter? pipeWriter,
    CancellationToken cancellationToken)
    : GethLikeBlockNativeTracer(null, txTracerFactory)
{
    protected override void AddTrace(GethLikeTxTrace trace) => Write(writer, pipeWriter, trace, cancellationToken);

    /// <summary>Writes <paramref name="trace"/> as one block-trace entry, flushes it to the response, and disposes it.</summary>
    public static void Write(Utf8JsonWriter writer, PipeWriter? pipeWriter, GethLikeTxTrace trace, CancellationToken cancellationToken)
    {
        using (trace)
        {
            GethLikeTxTraceCollectionConverter.WriteEntry(writer, trace, EthereumJsonSerializer.JsonOptions);
        }

        writer.Flush();
        pipeWriter?.FlushAsync(cancellationToken).SafeWait();
    }
}
