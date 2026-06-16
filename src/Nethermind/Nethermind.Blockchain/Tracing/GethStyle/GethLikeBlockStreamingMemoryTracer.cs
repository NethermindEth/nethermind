// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Extensions;

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
    : BlockTracerBase<GethLikeTxTrace, GethLikeTxDirectStreamingTracer>(options.TxHash), IDisposable
{
    private GethLikeTxDirectStreamingTracer? _reusableTxTracer;

    protected override GethLikeTxDirectStreamingTracer OnStart(Transaction? tx)
    {
        if (_reusableTxTracer is null)
        {
            _reusableTxTracer = new GethLikeTxDirectStreamingTracer(tx, options, writer, pipeWriter, cancellationToken);
        }
        else
        {
            _reusableTxTracer.ResetForNextTx(tx);
        }
        return _reusableTxTracer;
    }

    protected override GethLikeTxTrace OnEnd(GethLikeTxDirectStreamingTracer txTracer) => txTracer.BuildResult();

    public override void EndBlockTrace()
    {
        _reusableTxTracer?.ReleaseResources();
        base.EndBlockTrace();
    }

    public void Dispose()
    {
        _reusableTxTracer?.ReleaseResources();
        DisposableExtensions.DisposeAndNull(ref _reusableTxTracer);
    }
}
