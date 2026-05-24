// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;

namespace Nethermind.JsonRpc;

/// <summary>
/// Shared scaffolding for streaming JSON-RPC results that defer trace execution into
/// <see cref="WriteToAsync"/>. Owns the linked-cancellation + <see cref="Utf8JsonWriter"/>
/// lifecycle and the synchronous JSON-converter fallback; concrete subclasses supply only
/// the type-specific JSON content via <see cref="EmitContent"/>.
/// </summary>
/// <remarks>
/// Used by both the debug <c>debug_trace*</c> streaming results and the parity
/// <c>trace_*</c> streaming results. <c>GethLikeTxTraceStreamingSingleResult</c> cannot
/// inherit this base because it already inherits
/// <c>Blockchain.Tracing.GethStyle.GethLikeTxTrace</c> to satisfy the
/// <c>ResultWrapper&lt;GethLikeTxTrace&gt;</c> return-type contract; it keeps its own
/// matching scaffolding inline.
/// </remarks>
public abstract class StreamingResultBase : IStreamableResult, IDisposable
{
    protected static readonly JsonWriterOptions StreamingWriterOptions = new() { SkipValidation = true };

    private readonly CancellationTokenSource _timeoutCts;
    private int _consumed;
    protected readonly CancellationToken TimeoutToken;
    protected readonly ILogger Logger;

    protected StreamingResultBase(CancellationTokenSource timeoutCts, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(timeoutCts);

        _timeoutCts = timeoutCts;
        TimeoutToken = timeoutCts.Token;
        Logger = logger;
    }

    public async ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken)
    {
        ThrowIfAlreadyConsumed();

        using CancellationTokenSource linkedCts =
            CancellationTokenSource.CreateLinkedTokenSource(TimeoutToken, cancellationToken);
        CancellationToken combinedToken = linkedCts.Token;

        using Utf8JsonWriter jsonWriter = new(writer, StreamingWriterOptions);

        try
        {
            EmitContent(jsonWriter, writer, combinedToken);
            jsonWriter.Flush();
            await writer.FlushAsync(combinedToken);
        }
        catch (OperationCanceledException) when (combinedToken.IsCancellationRequested)
        {
            if (Logger.IsDebug) Logger.Debug("Streaming JSON-RPC response cancelled mid-emit; client receives a partial body with the JSON envelope closed by the inner finally blocks.");
        }
    }

    /// <summary>
    /// Synchronous emission for the fallback path used by the JSON converter when the
    /// caller does not detect <see cref="IStreamableResult"/> (e.g. test infrastructure
    /// or batch responses). The supplied writer is fed by the caller's buffer.
    /// </summary>
    internal void WriteAsJson(Utf8JsonWriter writer)
    {
        ThrowIfAlreadyConsumed();
        EmitContent(writer, pipeWriter: null, TimeoutToken);
    }

    /// <summary>
    /// Streaming results wrap deferred trace delegates that mutate worldstate
    /// (<c>SkipValidationAndCommit</c>) and are therefore single-shot. A second emit would
    /// re-run the trace on post-mutation state and silently corrupt the output; surface it
    /// loudly instead. Production HTTP pipelines invoke the result exactly once; this guard
    /// only fires when test infrastructure or future runner code accidentally re-serialises.
    /// </summary>
    private void ThrowIfAlreadyConsumed()
    {
        if (Interlocked.Exchange(ref _consumed, 1) != 0)
        {
            throw new InvalidOperationException(
                $"{GetType().Name} is single-invocation; a second WriteToAsync/WriteAsJson would re-execute a non-idempotent trace delegate.");
        }
    }

    /// <summary>
    /// Concrete subclasses emit the type-specific JSON shape here. <paramref name="pipeWriter"/>
    /// is non-null on the streaming path (use it for periodic <see cref="PipeWriter.FlushAsync"/>
    /// calls) and null on the synchronous-fallback path.
    /// </summary>
    protected abstract void EmitContent(Utf8JsonWriter writer, PipeWriter? pipeWriter, CancellationToken cancellationToken);

    public virtual void Dispose() => _timeoutCts.Dispose();
}
