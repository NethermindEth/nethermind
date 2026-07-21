// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;

namespace Nethermind.JsonRpc;

/// <summary>
/// Shared lifecycle support for streamable JSON-RPC results.
/// </summary>
public abstract class StreamingResultBase(CancellationTokenSource timeoutCts, ILogger logger) : IDisposable
{
    internal static readonly JsonWriterOptions WriterOptions = new() { SkipValidation = true };

    private readonly CancellationTokenSource _timeoutCts = timeoutCts ?? throw new ArgumentNullException(nameof(timeoutCts));

    protected ILogger Logger { get; } = logger;
    protected CancellationToken TimeoutToken { get; } = timeoutCts.Token;

    internal static async ValueTask WriteJsonToAsync(
        CancellationToken timeoutToken,
        ILogger logger,
        PipeWriter writer,
        Action<Utf8JsonWriter, PipeWriter?, CancellationToken> emitContent,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutToken, cancellationToken);
        CancellationToken combinedToken = linkedCts.Token;

        using Utf8JsonWriter jsonWriter = new(writer, WriterOptions);

        try
        {
            emitContent(jsonWriter, writer, combinedToken);
            jsonWriter.Flush();
            await writer.FlushAsync(combinedToken);
        }
        catch (OperationCanceledException) when (combinedToken.IsCancellationRequested)
        {
            if (logger.IsDebug) logger.Debug("JSON-RPC streaming cancelled mid-response; client receives a partial body with the JSON envelope closed by the inner finally blocks.");
        }
    }

    protected static async ValueTask<StreamableResultStatus> WriteToWithStatusAsync(
        CancellationToken timeoutToken,
        ILogger logger,
        Func<CancellationToken, ValueTask<StreamableResultStatus>> emitContent,
        CancellationToken cancellationToken,
        string timeoutLogMessage,
        string cancellationLogMessage)
    {
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutToken, cancellationToken);
        CancellationToken combinedToken = linkedCts.Token;

        try
        {
            return await emitContent(combinedToken);
        }
        catch (OperationCanceledException) when (timeoutToken.IsCancellationRequested)
        {
            if (logger.IsDebug) logger.Debug(timeoutLogMessage);
            return StreamableResultStatus.Timeout;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (logger.IsDebug) logger.Debug(cancellationLogMessage);
            return StreamableResultStatus.Cancelled;
        }
    }

    public virtual void Dispose() => _timeoutCts.Dispose();
}

/// <summary>
/// Base class for streamable results that emit their result through a <see cref="Utf8JsonWriter"/>.
/// </summary>
public abstract class JsonStreamingResultBase(CancellationTokenSource timeoutCts, ILogger logger)
    : StreamingResultBase(timeoutCts, logger), IStreamableResult
{
    public ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken)
        => WriteJsonToAsync(TimeoutToken, Logger, writer, EmitContent, cancellationToken);

    internal void WriteAsJson(Utf8JsonWriter writer) => EmitContent(writer, null, TimeoutToken);

    protected abstract void EmitContent(Utf8JsonWriter writer, PipeWriter? pipeWriter, CancellationToken cancellationToken);
}
