// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.DebugModule;

/// <summary>
/// Shared scaffolding for streaming JSON-RPC results that defer trace execution into
/// <see cref="WriteToAsync"/>. Owns the linked-cancellation + <see cref="Utf8JsonWriter"/>
/// lifecycle and the synchronous JSON-converter fallback; concrete subclasses supply only
/// the type-specific JSON content via <see cref="EmitContent"/>.
/// </summary>
/// <remarks>
/// <see cref="GethLikeTxTraceStreamingSingleResult"/> cannot inherit this base because it
/// already inherits <see cref="Blockchain.Tracing.GethStyle.GethLikeTxTrace"/> to satisfy
/// the <c>ResultWrapper&lt;GethLikeTxTrace&gt;</c> return-type contract; it keeps its own
/// matching scaffolding inline.
/// </remarks>
public abstract class StreamingResultBase : IStreamableResult, IDisposable
{
    protected static readonly JsonWriterOptions StreamingWriterOptions = new() { SkipValidation = true };

    private readonly CancellationTokenSource _timeoutCts;
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
        using CancellationTokenSource linkedCts =
            CancellationTokenSource.CreateLinkedTokenSource(TimeoutToken, cancellationToken);
        CancellationToken combinedToken = linkedCts.Token;

        using Utf8JsonWriter jsonWriter = new(writer, StreamingWriterOptions);

        EmitContent(jsonWriter, writer, combinedToken);

        jsonWriter.Flush();
        await writer.FlushAsync(combinedToken);
    }

    /// <summary>
    /// Synchronous emission for the fallback path used by the JSON converter when the
    /// caller does not detect <see cref="IStreamableResult"/> (e.g. test infrastructure
    /// or batch responses). The supplied writer is fed by the caller's buffer.
    /// </summary>
    internal void WriteAsJson(Utf8JsonWriter writer) => EmitContent(writer, pipeWriter: null, TimeoutToken);

    /// <summary>
    /// Concrete subclasses emit the type-specific JSON shape here. <paramref name="pipeWriter"/>
    /// is non-null on the streaming path (use it for periodic <see cref="PipeWriter.FlushAsync"/>
    /// calls) and null on the synchronous-fallback path.
    /// </summary>
    protected abstract void EmitContent(Utf8JsonWriter writer, PipeWriter? pipeWriter, CancellationToken cancellationToken);

    public virtual void Dispose() => _timeoutCts.Dispose();
}
