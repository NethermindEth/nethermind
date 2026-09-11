// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Abstractions;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Nethermind.Config;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.JsonRpc;

/// <summary>Reads JSON-RPC requests off a transport and writes the responses to a sink.</summary>
/// <remarks>
/// Owns the transport-facing state machine only: the pipe read loop, the complete-body fast path, batch iteration
/// and per-request dispatch classification. Decoding bytes into requests belongs to
/// <see cref="JsonRpcRequestDecoder"/>, batch enumeration to <see cref="IJsonRpcBatchItemSource"/>, and recording
/// and tracing to <see cref="JsonRpcDiagnostics"/>.
/// </remarks>
public sealed class JsonRpcProcessor : IJsonRpcProcessor
{
    private readonly IJsonRpcConfig _jsonRpcConfig;
    private readonly ILogger _logger;
    private readonly IJsonRpcService _jsonRpcService;
    private readonly JsonRpcDiagnostics _diagnostics;
    private readonly IProcessExitSource? _processExitSource;

    public JsonRpcProcessor(IJsonRpcService jsonRpcService, IJsonRpcConfig jsonRpcConfig, IFileSystem fileSystem, ILogManager logManager, IProcessExitSource? processExitSource = null)
    {
        _logger = logManager?.GetClassLogger<JsonRpcProcessor>() ?? throw new ArgumentNullException(nameof(logManager));
        ArgumentNullException.ThrowIfNull(fileSystem);

        _jsonRpcService = jsonRpcService ?? throw new ArgumentNullException(nameof(jsonRpcService));
        _jsonRpcConfig = jsonRpcConfig ?? throw new ArgumentNullException(nameof(jsonRpcConfig));

        _processExitSource = processExitSource;
        _diagnostics = new JsonRpcDiagnostics(_jsonRpcConfig, fileSystem, _logger);
    }

    public CancellationToken ProcessExit => _processExitSource?.Token ?? default;

    public ValueTask ProcessAsync(
        PipeReader reader,
        JsonRpcContext context,
        IJsonRpcResponseSink sink,
        JsonRpcProcessingOptions options,
        CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? timeoutSource = BeginRequest(context);

        return ProcessCoreAsync(reader, context, sink, options, timeoutSource, timeoutSource?.Token ?? CancellationToken.None, cancellationToken);
    }

    public ValueTask ProcessAsync(
        ReadOnlyMemory<byte> requestBody,
        JsonRpcContext context,
        IJsonRpcResponseSink sink,
        JsonRpcProcessingOptions options,
        CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? timeoutSource = BeginRequest(context);

        return ProcessMemoryCoreAsync(requestBody, context, sink, options, timeoutSource, timeoutSource?.Token ?? CancellationToken.None, cancellationToken);
    }

    /// <summary>Publishes the ambient context and takes a timeout budget for callers that are subject to one.</summary>
    /// <returns>The pooled source the caller must return, or <c>null</c> when the caller is not timed out.</returns>
    private CancellationTokenSource? BeginRequest(JsonRpcContext context)
    {
        JsonRpcContext.Current.Value = context;

        return context.IsAuthenticated ? null : _jsonRpcConfig.BuildTimeoutCancellationToken();
    }

    private async ValueTask ProcessMemoryCoreAsync(
        ReadOnlyMemory<byte> requestBody,
        JsonRpcContext context,
        IJsonRpcResponseSink sink,
        JsonRpcProcessingOptions options,
        CancellationTokenSource? timeoutSource,
        CancellationToken timeoutToken,
        CancellationToken cancellationToken)
    {
        try
        {
            if (ProcessExit.IsCancellationRequested)
            {
                await WriteShutdownResponseAsync(sink, cancellationToken);
                return;
            }

            if (options.InputMode != JsonRpcInputMode.SingleDocument)
            {
                PipeReader reader = PipeReader.Create(new ReadOnlySequence<byte>(requestBody));
                // Hand the timeout budget over: ProcessCoreAsync returns it in its own finally, so this one must
                // not return it a second time.
                CancellationTokenSource? coreTimeoutSource = timeoutSource;
                timeoutSource = null;
                await ProcessCoreAsync(reader, context, sink, options, coreTimeoutSource, timeoutToken, cancellationToken);
                return;
            }

            _diagnostics.RecordRequest(requestBody);

            await ProcessSingleDocumentMemoryToSink(requestBody, context, sink, options, cancellationToken);
        }
        finally
        {
            if (timeoutSource is not null)
                JsonRpcConfigExtension.ReturnTimeoutCancellationToken(timeoutSource);
        }
    }

    private async ValueTask ProcessCoreAsync(
        PipeReader reader,
        JsonRpcContext context,
        IJsonRpcResponseSink sink,
        JsonRpcProcessingOptions options,
        CancellationTokenSource? timeoutSource,
        CancellationToken timeoutToken,
        CancellationToken cancellationToken,
        bool recordRequest = true)
    {
        using PipeJsonProcessingState processingState = new(JsonRpcRequestDecoder.CreateJsonReaderState(options));
        try
        {
            if (ProcessExit.IsCancellationRequested)
            {
                await WriteShutdownResponseAsync(sink, cancellationToken);
                return;
            }

            if (recordRequest)
            {
                reader = await _diagnostics.RecordRequest(reader);
            }

            while (!processingState.ShouldExit)
            {
                long startTime = Stopwatch.GetTimestamp();
                ReadResult readResult;
                try
                {
                    readResult = await reader.ReadAsync(timeoutToken);
                }
                catch (BadHttpRequestException e)
                {
                    Handle(e);
                    break;
                }
                catch (ConnectionResetException e)
                {
                    Handle(e);
                    break;
                }

                await ProcessReadResultToSink(reader, readResult, processingState, context, sink, options, startTime, cancellationToken);
            }
        }
        finally
        {
            await reader.CompleteAsync();
            if (timeoutSource is not null)
                JsonRpcConfigExtension.ReturnTimeoutCancellationToken(timeoutSource);
        }
    }

    private async ValueTask ProcessReadResultToSink(
        PipeReader reader,
        ReadResult readResult,
        PipeJsonProcessingState processingState,
        JsonRpcContext context,
        IJsonRpcResponseSink sink,
        JsonRpcProcessingOptions options,
        long startTime,
        CancellationToken cancellationToken)
    {
        ReadOnlySequence<byte> buffer = readResult.Buffer;
        PipeAdvance advance = new(reader);

        try
        {
            bool isCompleted = readResult.IsCompleted || readResult.IsCanceled;
            JsonRpcResult.Entry? result = null;

            if (processingState.HasPendingSingleDocument)
            {
                JsonDocument pendingDocument = processingState.TakePendingSingleDocument(out long pendingStartTime);
                result = await ProcessSingleDocumentToSink(processingState, pendingDocument, pendingStartTime, buffer, isCompleted, context, sink, options, cancellationToken);
                advance.Consumed(in buffer);
            }
            else
            {
                if (processingState.FreshState)
                {
                    buffer = buffer.TrimStart();
                }

                if (buffer.IsEmpty && readResult.IsCompleted && options.InputMode == JsonRpcInputMode.SingleDocument)
                {
                    result = GetParsingError(startTime, in buffer, context, "Error during parsing/validation: empty request.");
                    processingState.ShouldExit = true;
                }
                else if (!buffer.IsEmpty)
                {
                    try
                    {
                        bool handledAsCompleteBody = false;
                        if (options.InputMode == JsonRpcInputMode.SingleDocument && isCompleted && buffer.IsSingleSegment)
                        {
                            CompleteBodyOutcome outcome;
                            JsonRpcResult.Entry? entry;
                            try
                            {
                                (outcome, entry) = await TryProcessCompleteBodyAsync(buffer.First, context, sink, startTime, cancellationToken);
                            }
                            catch
                            {
                                // Whatever throws out of that call - a dispatch or a sink write - ends the read
                                // loop, and this branch already knows the buffer holds the whole body, so report
                                // it consumed as a normal return would.
                                advance.Consumed(in buffer);
                                throw;
                            }

                            if (outcome != CompleteBodyOutcome.NotApplicable)
                            {
                                // Answered, with responses or with a parse error: a complete single-segment body is
                                // the whole request either way, so nothing is left for another read.
                                handledAsCompleteBody = true;
                                processingState.ShouldExit = true;
                                result = entry;
                                advance.Consumed(in buffer);
                            }
                        }

                        if (!handledAsCompleteBody)
                        {
                            JsonDocument? jsonDocument = null;
                            bool undecodable = false;
                            try
                            {
                                // Decode only; see JsonRpcRequestDecoder.IsRequestDecodingException for why this
                                // cannot be folded into the outer catch.
                                processingState.FreshState = JsonRpcRequestDecoder.TryParseJson(ref buffer, isCompleted, ref processingState.ReaderState, out jsonDocument, options);
                            }
                            catch (Exception ex) when (JsonRpcRequestDecoder.IsRequestDecodingException(ex))
                            {
                                result = GetParsingError(startTime, in buffer, context, "Error during parsing/validation.", ex);
                                processingState.ShouldExit = true;
                                undecodable = true;
                                advance.Consumed(in buffer);
                            }

                            if (!undecodable && processingState.FreshState)
                            {
                                if (options.InputMode == JsonRpcInputMode.SingleDocument)
                                {
                                    result = await ProcessSingleDocumentToSink(processingState, jsonDocument, startTime, buffer, isCompleted, context, sink, options, cancellationToken);
                                    advance.Consumed(in buffer);
                                }
                                else
                                {
                                    await ProcessJsonDocumentToSink(jsonDocument, context, sink, options, startTime, cancellationToken);
                                }
                            }
                            else if (!undecodable && isCompleted && !buffer.IsEmpty)
                            {
                                result = GetParsingError(startTime, in buffer, context, "Error during parsing/validation: incomplete request.");
                                processingState.ShouldExit = true;
                            }

                            if (!advance.Reported)
                            {
                                advance.Examined(in buffer);
                            }
                        }
                    }
                    catch (BadHttpRequestException e)
                    {
                        Handle(e);
                        processingState.ShouldExit = true;
                    }
                    catch (ConnectionResetException e)
                    {
                        Handle(e);
                        processingState.ShouldExit = true;
                    }
                    catch (JsonException ex)
                    {
                        // Deliberately NOT IsRequestDecodingException: this catch wraps request *execution* as
                        // well as decoding. See JsonRpcRequestDecoder.IsRequestDecodingException.
                        result = GetParsingError(startTime, in buffer, context, "Error during parsing/validation.", ex);
                        processingState.ShouldExit = true;
                    }
                }
            }

            if (result.HasValue)
            {
                await WriteSingleEntryAsync(result.Value, sink, cancellationToken);
            }

            processingState.ShouldExit |= isCompleted && buffer.IsEmpty;
        }
        finally
        {
            if (!advance.Reported)
            {
                advance.Examined(in buffer);
            }
        }
    }

    /// <summary>Ends a single-document read: reject trailing data, dispatch once the body is complete, or hold it.</summary>
    /// <param name="startTime">When this document's request started - for a held document, the read that parsed it.</param>
    /// <param name="trailingBuffer">Whatever follows the document. Anything but whitespace is an error.</param>
    /// <returns>The parse error to write, or <c>null</c> when there is nothing to answer yet.</returns>
    /// <remarks>
    /// Takes ownership of <paramref name="jsonDocument"/>: it is disposed on the error path, handed to
    /// <see cref="ProcessJsonDocumentToSink"/> (which disposes it) once the body is complete, or handed back to
    /// <paramref name="processingState"/> to be disposed with it.
    /// </remarks>
    private async ValueTask<JsonRpcResult.Entry?> ProcessSingleDocumentToSink(
        PipeJsonProcessingState processingState,
        JsonDocument jsonDocument,
        long startTime,
        ReadOnlySequence<byte> trailingBuffer,
        bool isCompleted,
        JsonRpcContext context,
        IJsonRpcResponseSink sink,
        JsonRpcProcessingOptions options,
        CancellationToken cancellationToken)
    {
        trailingBuffer = trailingBuffer.TrimStart();
        if (!trailingBuffer.IsEmpty)
        {
            jsonDocument.Dispose();
            processingState.ShouldExit = true;
            return GetParsingError(startTime, in trailingBuffer, context, "Error during parsing/validation: trailing data after JSON-RPC request.");
        }

        if (isCompleted)
        {
            await ProcessJsonDocumentToSink(jsonDocument, context, sink, options, startTime, cancellationToken);
            processingState.ShouldExit = true;
        }
        else
        {
            processingState.SetPendingSingleDocument(jsonDocument, startTime);
        }

        return null;
    }

    private async ValueTask ProcessSingleDocumentMemoryToSink(
        ReadOnlyMemory<byte> requestBody,
        JsonRpcContext context,
        IJsonRpcResponseSink sink,
        JsonRpcProcessingOptions options,
        CancellationToken cancellationToken)
    {
        long startTime = Stopwatch.GetTimestamp();

        (CompleteBodyOutcome outcome, JsonRpcResult.Entry? entry) =
            await TryProcessCompleteBodyAsync(requestBody, context, sink, startTime, cancellationToken);

        if (outcome == CompleteBodyOutcome.Handled)
        {
            return;
        }

        if (outcome == CompleteBodyOutcome.ParseError)
        {
            await WriteSingleEntryAsync(entry!.Value, sink, cancellationToken);
            return;
        }

        try
        {
            PipeReader reader = PipeReader.Create(new ReadOnlySequence<byte>(requestBody));
            await ProcessCoreAsync(reader, context, sink, options, timeoutSource: null, timeoutToken: CancellationToken.None, cancellationToken, recordRequest: false);
        }
        catch (JsonException ex)
        {
            await WriteParsingErrorAsync(new ReadOnlySequence<byte>(requestBody), context, sink, startTime, "Error during parsing/validation.", cancellationToken, ex);
        }
    }

    private enum CompleteBodyOutcome
    {
        /// <summary>Not one complete JSON-RPC document; the caller must fall back to the incremental parser.</summary>
        NotApplicable,

        /// <summary>Answered in full - one request, or a whole batch.</summary>
        Handled,

        /// <summary>Undecodable; the returned entry is the parse error to write.</summary>
        ParseError,
    }

    /// <summary>Answers a request body that is already complete in memory: one JSON-RPC object, or one batch array.</summary>
    /// <remarks>
    /// Shared by the direct-memory transport and the pipe loop's complete-body fast path, so the two cannot drift on
    /// which shapes take the fast route or on where the decode guard sits. Only the decode is guarded (see
    /// <see cref="JsonRpcRequestDecoder.IsRequestDecodingException"/>); dispatching a decoded request happens outside
    /// it, so a node fault surfacing as an <see cref="InvalidOperationException"/> is not answered as a parse error.
    /// <para>
    /// The <see cref="JsonException"/> catch around the batch run is knowingly wider than a decode: a serialization
    /// failure part-way through a batch is answered -32700, which the sink appends after
    /// <c>EndBatchAsync</c> has already closed the array. Narrowing it to the envelope decode would turn that
    /// malformed 200 into a 500, so the scope is a client-visible contract and not to be changed incidentally.
    /// </para>
    /// </remarks>
    private async ValueTask<(CompleteBodyOutcome Outcome, JsonRpcResult.Entry? Entry)> TryProcessCompleteBodyAsync(
        ReadOnlyMemory<byte> body,
        JsonRpcContext context,
        IJsonRpcResponseSink sink,
        long startTime,
        CancellationToken cancellationToken)
    {
        JsonRpcRequest? directRequest = null;
        ReadOnlyMemory<byte> batchBody = default;
        bool isBatch = false;

        try
        {
            if (!JsonRpcRequestDecoder.TryReadSingleObjectRequest(body, out directRequest))
            {
                isBatch = JsonRpcRequestDecoder.TryGetSingleDocumentBody(body, JsonTokenType.StartArray, out batchBody);
            }
        }
        catch (Exception ex) when (JsonRpcRequestDecoder.IsRequestDecodingException(ex))
        {
            return (CompleteBodyOutcome.ParseError, CreateBodyParsingError(body, context, startTime, ex));
        }

        if (directRequest is not null)
        {
            await ProcessSingleRequestToSink(directRequest, context, sink, cancellationToken);
            return (CompleteBodyOutcome.Handled, null);
        }

        if (!isBatch)
        {
            return (CompleteBodyOutcome.NotApplicable, null);
        }

        try
        {
            await RunBatchAsync(new MemoryBatchItemSource(batchBody), context, sink, cancellationToken);
        }
        catch (JsonException ex)
        {
            return (CompleteBodyOutcome.ParseError, CreateBodyParsingError(body, context, startTime, ex));
        }

        return (CompleteBodyOutcome.Handled, null);
    }

    private JsonRpcResult.Entry CreateBodyParsingError(ReadOnlyMemory<byte> body, JsonRpcContext context, long startTime, Exception exception)
    {
        ReadOnlySequence<byte> bodySequence = new(body);
        return GetParsingError(startTime, in bodySequence, context, "Error during parsing/validation.", exception);
    }

    private void Handle(ConnectionResetException e)
    {
        if (_logger.IsTrace) _logger.Trace($"Connection reset.{Environment.NewLine}{e}");
    }

    private void Handle(BadHttpRequestException e)
    {
        Metrics.JsonRpcRequestDeserializationFailures++;
        if (_logger.IsDebug) _logger.Debug($"Couldn't read request.{Environment.NewLine}{e}");
    }

    private async ValueTask ProcessJsonDocumentToSink(
        JsonDocument jsonDocument,
        JsonRpcContext context,
        IJsonRpcResponseSink sink,
        JsonRpcProcessingOptions options,
        long startTime,
        CancellationToken cancellationToken)
    {
        try
        {
            JsonElement rootElement = jsonDocument.RootElement;
            switch (rootElement.ValueKind)
            {
                case JsonValueKind.Object:
                    JsonRpcRequest request;
                    try
                    {
                        // Decode only; see JsonRpcRequestDecoder.IsRequestDecodingException.
                        request = JsonRpcRequestDecoder.CreateRequest(rootElement);
                    }
                    catch (Exception ex) when (JsonRpcRequestDecoder.IsRequestDecodingException(ex))
                    {
                        // -32700, not -32600: the bytes never decoded into a request, so there is nothing to
                        // call invalid. The raw buffer is not available here (the document is already parsed),
                        // and GetParsingError only uses it to enrich the debug log.
                        await WriteParsingErrorAsync(default, context, sink, startTime, "Error during parsing/validation.", cancellationToken, ex);
                        break;
                    }

                    if (_logger.IsDebug) DebugRequest(request);

                    JsonRpcResult.Entry singleResponse = await HandleSingleRequest(request, context);
                    await WriteSingleEntryAsync(singleResponse, sink, cancellationToken);
                    break;

                case JsonValueKind.Array:
                    await RunBatchAsync(new DocumentBatchItemSource(rootElement), context, sink, cancellationToken);
                    break;

                default:
                    await WriteInvalidRequestAsync(sink, startTime, cancellationToken);
                    break;
            }
        }
        finally
        {
            jsonDocument.Dispose();
        }
    }

    private async ValueTask ProcessSingleRequestToSink(
        JsonRpcRequest request,
        JsonRpcContext context,
        IJsonRpcResponseSink sink,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_logger.IsDebug) DebugRequest(request);

            JsonRpcResult.Entry response = await HandleSingleRequest(request, context);
            await WriteSingleEntryAsync(response, sink, cancellationToken);
        }
        finally
        {
            request.DisposeParsedParamsDocument();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void DebugRequest(JsonRpcRequest request) =>
        _logger.Debug($"JSON RPC request {request.Method}");

    /// <summary>Runs one JSON-RPC batch: size limit, then decode-dispatch-write per item, then close the array.</summary>
    /// <remarks>
    /// One loop for both transports: the raw-bytes and parsed-document paths differ only in how items are
    /// enumerated, which <typeparamref name="TSource"/> supplies. Constrained to <c>struct</c> so the calls into it
    /// do not box and it JITs once per source; that also means <paramref name="source"/> is iterated in place, so
    /// its cursor must be mutable state on the struct rather than a copy.
    /// </remarks>
    private async ValueTask RunBatchAsync<TSource>(
        TSource source,
        JsonRpcContext context,
        IJsonRpcResponseSink sink,
        CancellationToken cancellationToken)
        where TSource : struct, IJsonRpcBatchItemSource
    {
        // Counting a batch of raw bytes means a second pass over them, so it is only paid for when the limit it
        // feeds actually applies. A parsed document already knows its length.
        int? requestCount = source.KnownCount ?? (context.IsAuthenticated ? null : source.ScanCount());
        if (!context.IsAuthenticated && requestCount > _jsonRpcConfig.MaxBatchSize)
        {
            await WriteBatchSizeLimitErrorAsync(requestCount.Value, sink, cancellationToken);
            return;
        }

        if (_logger.IsDebug)
        {
            _logger.Debug(requestCount is null ? "JSON RPC batch request" : $"{requestCount} JSON RPC requests");
        }

        await sink.BeginBatchAsync(cancellationToken);
        long startTime = Stopwatch.GetTimestamp();
        int requestIndex = 0;
        bool isStopped = false;
        BatchRequestJsonLifetime batchRequestJsonLifetime = new();

        try
        {
            while (source.TryGetNext(out JsonRpcRequest? request, out JsonDocument? ownedRequestDocument, out Exception? decodeException))
            {
                requestIndex++;
                if (request is null)
                {
                    if (decodeException is not null) LogInvalidBatchItem(decodeException);
                    await WriteBatchEntryAsync(CreateInvalidRequestEntry(startTime), sink, cancellationToken);
                    // The entry still counts against the response body: skipping this read would let the next
                    // valid element be dispatched in full after the sink already asked to stop.
                    isStopped |= sink.StopRequested;
                    continue;
                }

                request.IsBatchItem = true;
                batchRequestJsonLifetime.TrackUntilBatchEnd(request, ownedRequestDocument);

                JsonRpcResult.Entry response = isStopped
                    ? CreateBatchResponseLimitEntry(request)
                    : await HandleSingleRequest(request, context);

                if (_logger.IsTrace)
                {
                    string progress = requestCount is null ? requestIndex.ToString() : $"{requestIndex}/{requestCount}";
                    _logger.Trace($"  {progress} JSON RPC request - {request} handled after {response.Report.HandlingTimeMicroseconds}");
                    _diagnostics.TraceResult(response);
                }

                await WriteBatchEntryAsync(response, sink, cancellationToken);
                isStopped |= sink.StopRequested;
            }

            if (_logger.IsTrace) _logger.Trace($"  {requestIndex} requests handled in {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds:N0}ms");
        }
        finally
        {
            try
            {
                await sink.EndBatchAsync(cancellationToken);
            }
            finally
            {
                batchRequestJsonLifetime.Dispose();
            }
        }
    }

    private void LogInvalidBatchItem(Exception exception)
    {
        if (_logger.IsDebug) _logger.Debug($"Invalid JSON-RPC batch item.{Environment.NewLine}{exception}");
    }

    private ValueTask WriteInvalidRequestAsync(
        IJsonRpcResponseSink sink,
        long startTime,
        CancellationToken cancellationToken) =>
        WriteSingleEntryAsync(CreateInvalidRequestEntry(startTime), sink, cancellationToken);

    private JsonRpcResult.Entry CreateInvalidRequestEntry(long startTime)
    {
        Metrics.JsonRpcInvalidRequests++;
        JsonRpcErrorResponse invalidResponse = _jsonRpcService.GetErrorResponse(ErrorCodes.InvalidRequest, "Invalid request");

        if (_logger.IsTrace)
        {
            _diagnostics.TraceResult(invalidResponse);
            _logger.Trace($"  Failed request handled in {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds:N0}ms");
        }

        return new(invalidResponse, new RpcReport("# parsing error #", (long)Stopwatch.GetElapsedTime(startTime).TotalMicroseconds, false));
    }

    private async ValueTask WriteShutdownResponseAsync(IJsonRpcResponseSink sink, CancellationToken cancellationToken)
    {
        JsonRpcErrorResponse response = _jsonRpcService.GetErrorResponse(ErrorCodes.ResourceUnavailable, "Shutting down");
        using JsonRpcResult.Entry entry = _diagnostics.RecordResponse(response, new RpcReport("Shutdown", 0, false));
        await sink.WriteSingleAsync(entry.Response, entry.Report, cancellationToken);
    }

    private async ValueTask WriteBatchSizeLimitErrorAsync(int requestCount, IJsonRpcResponseSink sink, CancellationToken cancellationToken)
    {
        if (_logger.IsWarn) _logger.Warn($"The batch size limit was exceeded. The requested batch size {requestCount}, and the current config setting is JsonRpc.{nameof(_jsonRpcConfig.MaxBatchSize)} = {_jsonRpcConfig.MaxBatchSize}.");
        JsonRpcErrorResponse errorResponse = _jsonRpcService.GetErrorResponse(ErrorCodes.LimitExceeded, "Batch size limit exceeded");
        await WriteSingleEntryAsync(new JsonRpcResult.Entry(errorResponse, RpcReport.Error), sink, cancellationToken);
    }

    private JsonRpcResult.Entry CreateBatchResponseLimitEntry(JsonRpcRequest jsonRpcRequest) =>
        new(
            _jsonRpcService.GetErrorResponse(
                ErrorCodes.LimitExceeded,
                jsonRpcRequest.Method,
                in jsonRpcRequest.IdRef,
                $"{nameof(IJsonRpcConfig.MaxBatchResponseBodySize)} of {_jsonRpcConfig.MaxBatchResponseBodySize / 1.KB}KB exceeded"),
            RpcReport.Error);

    private async ValueTask WriteParsingErrorAsync(
        ReadOnlySequence<byte> buffer,
        JsonRpcContext context,
        IJsonRpcResponseSink sink,
        long startTime,
        string error,
        CancellationToken cancellationToken,
        Exception? exception = null)
    {
        JsonRpcResult.Entry result = GetParsingError(startTime, in buffer, context, error, exception);
        await WriteSingleEntryAsync(result, sink, cancellationToken);
    }

    private ValueTask WriteSingleEntryAsync(JsonRpcResult.Entry entry, IJsonRpcResponseSink sink, CancellationToken cancellationToken) =>
        WriteEntryAsync(entry, sink, isBatch: false, cancellationToken);

    private ValueTask WriteBatchEntryAsync(JsonRpcResult.Entry entry, IJsonRpcResponseSink sink, CancellationToken cancellationToken) =>
        WriteEntryAsync(entry, sink, isBatch: true, cancellationToken);

    private ValueTask WriteEntryAsync(
        JsonRpcResult.Entry entry,
        IJsonRpcResponseSink sink,
        bool isBatch,
        CancellationToken cancellationToken)
    {
        try
        {
            JsonRpcResult.Entry recorded = _diagnostics.RecordResponse(entry);
            ValueTask writeTask = isBatch
                ? sink.WriteBatchItemAsync(recorded.Response, recorded.Report, cancellationToken)
                : sink.WriteSingleAsync(recorded.Response, recorded.Report, cancellationToken);
            if (writeTask.IsCompletedSuccessfully)
            {
                writeTask.GetAwaiter().GetResult();
                entry.Dispose();
                return ValueTask.CompletedTask;
            }

            return AwaitAndDisposeAsync(writeTask, entry);
        }
        catch
        {
            entry.Dispose();
            throw;
        }
    }

    private static async ValueTask AwaitAndDisposeAsync(ValueTask writeTask, JsonRpcResult.Entry entry)
    {
        try
        {
            await writeTask;
        }
        finally
        {
            entry.Dispose();
        }
    }

    /// <remarks>
    /// -32700 is a request error like any other (#13156): bytes this node could not parse are the caller's fault,
    /// and one unauthenticated request must not put a line - let alone a stack trace - on the operator's console.
    /// The detail is kept, at Debug, by the same rule <see cref="IsDemotableRequestError"/> applies to the response
    /// path: a JWT-authenticated caller that cannot frame a JSON-RPC request is the operator's problem.
    /// </remarks>
    private JsonRpcResult.Entry GetParsingError(
        long startTime,
        ref readonly ReadOnlySequence<byte> buffer,
        JsonRpcContext context,
        string error,
        Exception? exception = null)
    {
        Metrics.JsonRpcRequestDeserializationFailures++;

        if (_logger.IsDebug)
        {
            const int sliceSize = 1000;
            if (Encoding.UTF8.TryGetStringSlice(in buffer, sliceSize, out bool isFullString, out string data))
            {
                error = isFullString
                    ? $"{error} Data:\n{data}\n"
                    : $"{error} Data (first {sliceSize} chars):\n{data[..sliceSize]}\n";
            }
        }

        // DebugError, not Debug, so the exception is still attached and a demoted line is still recognisable as
        // an error once the operator turns Debug on.
        if (context.IsAuthenticated)
        {
            if (_logger.IsError) _logger.Error(error, exception);
        }
        else
        {
            _logger.DebugError(error, exception);
        }

        JsonRpcErrorResponse response = _jsonRpcService.GetErrorResponse(ErrorCodes.ParseError, "parse error");
        _diagnostics.TraceResult(response);
        return _diagnostics.RecordResponse(response, new RpcReport("# parsing error #", (long)Stopwatch.GetElapsedTime(startTime).TotalMicroseconds, false));
    }

    private ValueTask<JsonRpcResult.Entry> HandleSingleRequest(JsonRpcRequest request, JsonRpcContext context)
    {
        Metrics.JsonRpcRequests++;
        long startTime = Stopwatch.GetTimestamp();

        ValueTask<JsonRpcResponse> responseTask = _jsonRpcService.SendRequestAsync(request, context);
        return responseTask.IsCompletedSuccessfully
            ? ValueTask.FromResult(CreateSingleRequestEntry(request, responseTask.Result, context, startTime))
            : AwaitAndCreateEntryAsync(responseTask, request, context, startTime);

        async ValueTask<JsonRpcResult.Entry> AwaitAndCreateEntryAsync(
            ValueTask<JsonRpcResponse> responseTask,
            JsonRpcRequest request,
            JsonRpcContext context,
            long startTime)
        {
            JsonRpcResponse response = await responseTask;
            return CreateSingleRequestEntry(request, response, context, startTime);
        }
    }

    private static string DescribeErrorResponse(JsonRpcRequest request, Error responseError) =>
        $"Error response handling JsonRpc Id:{request.Id} Method:{request.Method} | Code: {responseError.Code} Message: {responseError.Message}";

    /// <summary>
    /// Whether this error response describes a fault in the request rather than a condition of the node, and so must
    /// not be able to dictate the operator's WARN volume (#13156). Demoted lines stay available at Debug.
    /// </summary>
    /// <remarks>
    /// Only unauthenticated callers are demoted. The rationale for #13156 is that a client fault costs one
    /// unauthenticated request, which does not hold for the JWT-authenticated Engine endpoint: there, -32601 is the
    /// canonical consensus-client/execution-client version-mismatch signal and -32602 means the CL sent a payload
    /// this node could not bind, both of which are the operator's problem and have to stay visible at default level.
    /// <para>
    /// Server-side codes (-32603, -32000, timeouts, unsuppressed limits) keep WARN for every caller, and
    /// <see cref="Error.OperatorActionable"/> overrides the code: -32600 also carries "namespace X is disabled for
    /// this URL", which is a statement about this node's configuration.
    /// </para>
    /// </remarks>
    private static bool IsDemotableRequestError(Error responseError, JsonRpcContext context) =>
        !context.IsAuthenticated
        && ErrorCodes.IsRequestError(responseError.Code)
        && !responseError.OperatorActionable;

    private JsonRpcResult.Entry CreateSingleRequestEntry(JsonRpcRequest request, JsonRpcResponse response, JsonRpcContext context, long startTime)
    {
        bool isSuccess = !response.TryGetError(out Error? responseError);
        if (isSuccess)
        {
            if (_logger.IsTrace) _logger.Trace($"Responded to Id:{request.Id} Method:{request.Method} in {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds:N0}ms");
            Metrics.JsonRpcSuccesses++;
        }
        else
        {
            if (responseError?.SuppressWarning == false)
            {
                if (IsDemotableRequestError(responseError, context))
                {
                    if (_logger.IsDebug) _logger.Debug(DescribeErrorResponse(request, responseError));
                }
                else
                {
                    if (_logger.IsWarn) _logger.Warn(DescribeErrorResponse(request, responseError));
                }

                _diagnostics.TraceRequestError(request, response);
            }
            Metrics.JsonRpcErrors++;
        }

        string reportMethod = responseError?.Code == ErrorCodes.MethodNotFound
            ? RpcReport.UnknownMethod
            : request.Method;
        JsonRpcResult.Entry result = new(
            response,
            new RpcReport(reportMethod, (long)Stopwatch.GetElapsedTime(startTime).TotalMicroseconds, isSuccess));

        _diagnostics.TraceResult(result);
        return result;
    }

    /// <summary>
    /// Tracks whether this read has already told the <see cref="PipeReader"/> what it took, so the one
    /// <c>finally</c> can default to "examined everything, consumed nothing" without risking a second call.
    /// </summary>
    /// <remarks>
    /// The buffer is passed per call rather than captured, because the incremental parser slices it and the
    /// post-parse report has to name the sliced remainder.
    /// </remarks>
    private struct PipeAdvance(PipeReader reader)
    {
        public bool Reported { get; private set; }

        /// <summary>The buffer was turned into responses in full; nothing is left to read again.</summary>
        public void Consumed(in ReadOnlySequence<byte> buffer)
        {
            reader.AdvanceTo(buffer.End);
            Reported = true;
        }

        /// <summary>Everything was examined but nothing consumed, so the next read waits for more data.</summary>
        public void Examined(in ReadOnlySequence<byte> buffer)
        {
            reader.AdvanceTo(buffer.Start, buffer.End);
            Reported = true;
        }
    }

    private sealed class PipeJsonProcessingState(JsonReaderState readerState) : IDisposable
    {
        private JsonDocument? _pendingSingleDocument;
        private long _pendingSingleDocumentStartTime;

        public JsonReaderState ReaderState = readerState;
        public bool FreshState = true;
        public bool ShouldExit;

        /// <summary>A document parsed out of a body the transport has not ended yet, held until it does.</summary>
        public bool HasPendingSingleDocument => _pendingSingleDocument is not null;

        public void SetPendingSingleDocument(JsonDocument document, long startTime)
        {
            _pendingSingleDocument = document;
            _pendingSingleDocumentStartTime = startTime;
        }

        /// <summary>Hands the held document and its start time over; the caller becomes responsible for disposing it.</summary>
        public JsonDocument TakePendingSingleDocument(out long startTime)
        {
            JsonDocument document = _pendingSingleDocument!;
            _pendingSingleDocument = null;
            startTime = _pendingSingleDocumentStartTime;
            return document;
        }

        public void Dispose() => _pendingSingleDocument?.Dispose();
    }

    private sealed class BatchRequestJsonLifetime : IDisposable
    {
        private List<JsonDocument>? _ownedRequestDocuments;
        private List<JsonRpcRequest>? _requestsWithRawParams;

        public void TrackUntilBatchEnd(JsonRpcRequest request, JsonDocument? ownedRequestDocument)
        {
            if (ownedRequestDocument is not null)
            {
                _ownedRequestDocuments ??= [];
                _ownedRequestDocuments.Add(ownedRequestDocument);
            }
            else if (!request.ParamsUtf8.IsEmpty)
            {
                _requestsWithRawParams ??= [];
                _requestsWithRawParams.Add(request);
            }
        }

        public void Dispose()
        {
            if (_ownedRequestDocuments is not null)
            {
                foreach (JsonDocument requestDocument in _ownedRequestDocuments)
                {
                    requestDocument.Dispose();
                }
            }

            if (_requestsWithRawParams is not null)
            {
                foreach (JsonRpcRequest request in _requestsWithRawParams)
                {
                    request.DisposeParsedParamsDocument();
                }
            }
        }
    }
}
