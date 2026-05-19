// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using Nethermind.Core;

namespace Nethermind.Blockchain.Tracing.GethStyle;

/// <summary>
/// Block-level wrapper around <see cref="GethLikeTxStreamingMemoryTracer"/>.
/// Mirrors <see cref="GethLikeBlockMemoryTracer"/> but every per-tx tracer it
/// creates writes its struct-log entries straight to the supplied JSON writer.
/// </summary>
public sealed class GethLikeBlockStreamingMemoryTracer(
    GethTraceOptions options,
    Utf8JsonWriter writer,
    PipeWriter? pipeWriter,
    CancellationToken cancellationToken)
    : BlockTracerBase<GethLikeTxTrace, GethLikeTxStreamingMemoryTracer>(options.TxHash)
{
    protected override GethLikeTxStreamingMemoryTracer OnStart(Transaction? tx)
        => new(tx, options, writer, pipeWriter, cancellationToken);

    protected override GethLikeTxTrace OnEnd(GethLikeTxStreamingMemoryTracer txTracer) => txTracer.BuildResult();
}
