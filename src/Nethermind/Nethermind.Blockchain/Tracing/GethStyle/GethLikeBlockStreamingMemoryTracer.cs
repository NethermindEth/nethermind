// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using Nethermind.Core;

namespace Nethermind.Blockchain.Tracing.GethStyle;

/// <summary>
/// Block-level wrapper around <see cref="GethLikeTxDirectStreamingTracer"/>.
/// Mirrors <see cref="GethLikeBlockMemoryTracer"/> but every per-tx tracer it
/// creates writes its struct-log entries straight to the supplied JSON writer
/// without allocating per-opcode entries or cloning the storage dictionary.
/// </summary>
public sealed class GethLikeBlockStreamingMemoryTracer(
    GethTraceOptions options,
    Utf8JsonWriter writer,
    PipeWriter? pipeWriter,
    CancellationToken cancellationToken)
    : BlockTracerBase<GethLikeTxTrace, GethLikeTxDirectStreamingTracer>(options.TxHash)
{
    protected override GethLikeTxDirectStreamingTracer OnStart(Transaction? tx)
        => new(tx, options, writer, pipeWriter, cancellationToken);

    protected override GethLikeTxTrace OnEnd(GethLikeTxDirectStreamingTracer txTracer) => txTracer.BuildResult();
}
