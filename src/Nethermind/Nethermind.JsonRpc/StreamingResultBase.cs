// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Trie;

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
            if (logger.IsDebug) logger.Debug("JSON-RPC streaming cancelled mid-response; the partial body is left as-is because this overload does not own the envelope.");
        }
        finally
        {
            // The envelope's result member is already written, so an emitter that aborted before producing
            // any value would leave a dangling `"result":` and an unparseable body.
            if (jsonWriter.BytesCommitted == 0 && jsonWriter.BytesPending == 0)
            {
                jsonWriter.WriteNullValue();
                jsonWriter.Flush();
            }
        }
    }

    /// <summary>
    /// Writes the complete JSON-RPC response envelope around the streamed value emitted by <paramref name="emitContent"/>.
    /// </summary>
    /// <remarks>
    /// The envelope head is buffered in the <see cref="Utf8JsonWriter"/> instead of being pushed to the transport up
    /// front, so a result that fails before its first flush has nothing on the wire yet and is replaced by a proper
    /// JSON-RPC error envelope (#13153). Note the boundary is the first <em>flush</em>, not the first byte written:
    /// the repairable window is a whole <see cref="System.IO.Pipelines.PipeWriter"/> segment, and a failure with as
    /// much as ~288 kB of the value already buffered in-process is still framed cleanly.
    /// <para>
    /// A result that fails <em>after</em> its first flush is not repairable here. The emitters write their containers
    /// straight to the <see cref="Utf8JsonWriter"/> and none of them unwinds on the way out, so the buffered value is
    /// left at an arbitrary depth with an unknown mix of open objects and arrays; <see cref="Utf8JsonWriter"/> does
    /// not expose the container kinds, and <see cref="WriterOptions"/> sets <c>SkipValidation</c>, so appending a
    /// closing token would produce corrupt bytes rather than an exception. Appending a <c>_streamStatus</c> tail to
    /// such a value yields a well-formed-looking HTTP 200 carrying malformed JSON, which is worse than the 2.0.0-rc
    /// behaviour. The exception is therefore rethrown, aborting the response body exactly as 2.0.0-rc does, which is
    /// the only outcome the client can actually detect.
    /// </para>
    /// </remarks>
    internal static async ValueTask WriteJsonResponseToAsync(
        CancellationToken timeoutToken,
        ILogger logger,
        PipeWriter writer,
        JsonRpcResponse response,
        JsonSerializerOptions options,
        Action<Utf8JsonWriter, PipeWriter?, CancellationToken> emitContent,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutToken, cancellationToken);
        CancellationToken combinedToken = linkedCts.Token;

        using Utf8JsonWriter jsonWriter = new(writer, WriterOptions);
        JsonRpcResponseWriter.WriteStreamedResultStart(jsonWriter);

        try
        {
            emitContent(jsonWriter, writer, combinedToken);
            jsonWriter.Flush();
            await writer.FlushAsync(combinedToken);
        }
        catch (OperationCanceledException)
        {
            // A timeout or a client disconnect is not "a complete trace": never fall through to the success tail.
            bool timedOut = timeoutToken.IsCancellationRequested;
            if (jsonWriter.BytesCommitted == 0)
            {
                if (logger.IsDebug) logger.Debug($"JSON-RPC streaming {(timedOut ? "timed out" : "was cancelled")} before the first flush for response {response.Id}; replaced by a JSON-RPC error.");
                WriteCancellationEnvelope(jsonWriter, response, options, timedOut);
                return;
            }

            if (logger.IsDebug) logger.Debug($"JSON-RPC streaming {(timedOut ? "timed out" : "was cancelled")} after {jsonWriter.BytesCommitted} committed bytes for response {response.Id}; aborting the response body.");
            throw;
        }
        catch (Exception e)
        {
            LogStreamingFailure(logger, e, response);
            if (jsonWriter.BytesCommitted == 0)
            {
                WriteErrorEnvelope(jsonWriter, response, options, e);
                return;
            }

            throw;
        }

        WriteStreamedResultEnd(jsonWriter, response);
    }

    private static void WriteStreamedResultEnd(Utf8JsonWriter jsonWriter, JsonRpcResponse response) =>
        JsonRpcResponseWriter.WriteStreamedResultEnd(jsonWriter, null, in response.IdRef);

    private static void WriteCancellationEnvelope(Utf8JsonWriter jsonWriter, JsonRpcResponse response, JsonSerializerOptions options, bool timedOut)
    {
        // Nothing was committed to the transport, so the buffered head and partial value can simply be dropped.
        jsonWriter.Reset();
        Error error = timedOut
            ? new Error { Code = ErrorCodes.Timeout, Message = "Request timed out" }
            : new Error { Code = ErrorCodes.InternalError, Message = "Request cancelled" };
        JsonRpcResponseWriter.WriteErrorEnvelope(jsonWriter, error, options, in response.IdRef);
    }

    private static void WriteErrorEnvelope(Utf8JsonWriter jsonWriter, JsonRpcResponse response, JsonSerializerOptions options, Exception failure)
    {
        // Nothing was committed to the transport, so the buffered head and partial value can simply be dropped.
        jsonWriter.Reset();
        Error error = FindMissingTrieNode(failure) is { } missingNode
            ? new Error { Code = ErrorCodes.ResourceNotFound, Message = missingNode.Message }
            : new Error { Code = ErrorCodes.InternalError, Message = "Internal error" };
        JsonRpcResponseWriter.WriteErrorEnvelope(jsonWriter, error, options, in response.IdRef);
    }

    private static void LogStreamingFailure(ILogger logger, Exception failure, JsonRpcResponse response)
    {
        if (FindMissingTrieNode(failure) is { } missingNode)
        {
            // HasStateForBlock only checks the state root; subtree nodes can still be pruned out after a successful
            // guard, and a streamed result is the first to notice. Same code (-32000) and message as
            // JsonRpcService.HandleMissingTrieNode, but deliberately without its error.data: that field carries the
            // exception text out to an unauthenticated caller, and there is no reason for a streamed response to
            // start doing so. The full exception stays in this log line.
            if (logger.IsWarn) logger.Warn($"Missing trie node while streaming JSON-RPC response {response.Id}: {missingNode.Message}");
        }
        else if (logger.IsError)
        {
            logger.Error($"Error while streaming JSON-RPC response {response.Id}", failure);
        }
    }

    private static MissingTrieNodeException? FindMissingTrieNode(Exception failure)
    {
        for (Exception? current = failure; current is not null; current = current.InnerException)
        {
            if (current is MissingTrieNodeException missingNode)
            {
                return missingNode;
            }
        }

        return null;
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
    : StreamingResultBase(timeoutCts, logger), IEnvelopeOwningStreamableResult
{
    public ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken)
        => WriteJsonToAsync(TimeoutToken, Logger, writer, EmitContent, cancellationToken);

    ValueTask IEnvelopeOwningStreamableResult.WriteResponseAsync(PipeWriter writer, JsonRpcResponse response, JsonSerializerOptions options, CancellationToken cancellationToken)
        => WriteJsonResponseToAsync(TimeoutToken, Logger, writer, response, options, EmitContent, cancellationToken);

    internal void WriteAsJson(Utf8JsonWriter writer) => EmitContent(writer, null, TimeoutToken);

    protected abstract void EmitContent(Utf8JsonWriter writer, PipeWriter? pipeWriter, CancellationToken cancellationToken);
}
