// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
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
using Nethermind.Core.Resettables;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc;

public sealed class JsonRpcProcessor : IJsonRpcProcessor
{
    private static readonly SearchValues<byte> JsonWhitespace = SearchValues.Create(" \t\r\n"u8);

    private readonly IJsonRpcConfig _jsonRpcConfig;
    private readonly ILogger _logger;
    private readonly IJsonRpcService _jsonRpcService;
    private readonly Recorder _recorder;
    private readonly IProcessExitSource? _processExitSource;

    public JsonRpcProcessor(IJsonRpcService jsonRpcService, IJsonRpcConfig jsonRpcConfig, IFileSystem fileSystem, ILogManager logManager, IProcessExitSource? processExitSource = null)
    {
        _logger = logManager?.GetClassLogger<JsonRpcProcessor>() ?? throw new ArgumentNullException(nameof(logManager));
        ArgumentNullException.ThrowIfNull(fileSystem);

        _jsonRpcService = jsonRpcService ?? throw new ArgumentNullException(nameof(jsonRpcService));
        _jsonRpcConfig = jsonRpcConfig ?? throw new ArgumentNullException(nameof(jsonRpcConfig));

        _processExitSource = processExitSource;

        if (_jsonRpcConfig.RpcRecorderState != RpcRecorderState.None)
        {
            if (_logger.IsWarn) _logger.Warn("Enabling JSON RPC diagnostics recorder - this will affect performance and should be only used in a diagnostics mode.");
            string recorderBaseFilePath = _jsonRpcConfig.RpcRecorderBaseFilePath.GetApplicationResourcePath();
            _recorder = new Recorder(recorderBaseFilePath, fileSystem, _logger);
        }
    }

    public CancellationToken ProcessExit => _processExitSource?.Token ?? default;

    private static readonly JsonReaderOptions _socketJsonReaderOptions = new() { AllowMultipleValues = true };

    public ValueTask ProcessAsync(
        PipeReader reader,
        JsonRpcContext context,
        IJsonRpcResponseSink sink,
        JsonRpcProcessingOptions options,
        CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? timeoutSource = context.IsAuthenticated ? null : _jsonRpcConfig.BuildTimeoutCancellationToken();
        CancellationToken timeoutToken = timeoutSource?.Token ?? CancellationToken.None;

        return ProcessCoreAsync(reader, context, sink, options, timeoutSource, timeoutToken, cancellationToken);
    }

    public ValueTask ProcessAsync(
        ReadOnlyMemory<byte> requestBody,
        JsonRpcContext context,
        IJsonRpcResponseSink sink,
        JsonRpcProcessingOptions options,
        CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? timeoutSource = context.IsAuthenticated ? null : _jsonRpcConfig.BuildTimeoutCancellationToken();
        CancellationToken timeoutToken = timeoutSource?.Token ?? CancellationToken.None;

        return ProcessMemoryCoreAsync(requestBody, context, sink, options, timeoutSource, timeoutToken, cancellationToken);
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
                CancellationTokenSource? coreTimeoutSource = timeoutSource;
                timeoutSource = null;
                await ProcessCoreAsync(reader, context, sink, options, coreTimeoutSource, timeoutToken, cancellationToken);
                return;
            }

            if (IsRecordingRequest)
            {
                RecordRequest(requestBody);
            }

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
        CancellationToken cancellationToken)
    {
        JsonDocument? pendingSingleDocument = null;
        long pendingSingleDocumentStartTime = 0;
        try
        {
            if (ProcessExit.IsCancellationRequested)
            {
                await WriteShutdownResponseAsync(sink, cancellationToken);
                return;
            }

            if (IsRecordingRequest)
            {
                reader = await RecordRequest(reader);
            }

            JsonReaderState readerState = CreateJsonReaderState(options);
            bool freshState = true;
            bool shouldExit = false;

            while (!shouldExit)
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

                ReadOnlySequence<byte> buffer = readResult.Buffer;
                bool advanced = false;

                try
                {
                    bool isCompleted = readResult.IsCompleted || readResult.IsCanceled;
                    JsonRpcResult.Entry? result = null;

                    if (pendingSingleDocument is not null)
                    {
                        ReadOnlySequence<byte> trailingBuffer = buffer.TrimStart();
                        if (!trailingBuffer.IsEmpty)
                        {
                            pendingSingleDocument.Dispose();
                            pendingSingleDocument = null;
                            result = GetParsingError(pendingSingleDocumentStartTime, in trailingBuffer, "Error during parsing/validation: trailing data after JSON-RPC request.");
                            shouldExit = true;
                        }
                        else if (isCompleted)
                        {
                            JsonDocument jsonDocument = pendingSingleDocument;
                            pendingSingleDocument = null;
                            await ProcessJsonDocumentToSink(jsonDocument, context, sink, options, pendingSingleDocumentStartTime, cancellationToken);
                            shouldExit = true;
                        }

                        reader.AdvanceTo(buffer.End);
                        advanced = true;

                        if (result.HasValue)
                        {
                            await WriteSingleEntryAsync(result.Value, sink, cancellationToken);
                        }

                        shouldExit |= isCompleted;
                        continue;
                    }

                    if (freshState)
                    {
                        buffer = buffer.TrimStart();
                    }

                    if (!buffer.IsEmpty)
                    {
                        try
                        {
                            if (options.InputMode == JsonRpcInputMode.SingleDocument &&
                                isCompleted &&
                                buffer.IsSingleSegment)
                            {
                                if (TryReadSingleObjectRequest(buffer.First, out JsonRpcRequest? directRequest, out JsonDocument? directParamsDocument))
                                {
                                    shouldExit = true;

                                    try
                                    {
                                        await ProcessSingleRequestToSink(directRequest, directParamsDocument, context, sink, cancellationToken);
                                    }
                                    finally
                                    {
                                        reader.AdvanceTo(buffer.End);
                                        advanced = true;
                                    }
                                    continue;
                                }

                                if (await TryProcessBatchRequestDirectly(buffer.First, context, sink, cancellationToken))
                                {
                                    shouldExit = true;
                                    reader.AdvanceTo(buffer.End);
                                    advanced = true;
                                    continue;
                                }
                            }

                            freshState = TryParseJson(ref buffer, isCompleted, ref readerState, out JsonDocument? jsonDocument, options);
                            if (freshState)
                            {
                                if (options.InputMode == JsonRpcInputMode.SingleDocument)
                                {
                                    ReadOnlySequence<byte> trailingBuffer = buffer.TrimStart();
                                    if (!trailingBuffer.IsEmpty)
                                    {
                                        jsonDocument.Dispose();
                                        result = GetParsingError(startTime, in trailingBuffer, "Error during parsing/validation: trailing data after JSON-RPC request.");
                                        shouldExit = true;
                                    }
                                    else if (isCompleted)
                                    {
                                        await ProcessJsonDocumentToSink(jsonDocument, context, sink, options, startTime, cancellationToken);
                                        shouldExit = true;
                                    }
                                    else
                                    {
                                        pendingSingleDocument = jsonDocument;
                                        pendingSingleDocumentStartTime = startTime;
                                    }

                                    reader.AdvanceTo(buffer.End);
                                    advanced = true;
                                }
                                else
                                {
                                    await ProcessJsonDocumentToSink(jsonDocument, context, sink, options, startTime, cancellationToken);
                                }
                            }
                            else if (isCompleted && !buffer.IsEmpty)
                            {
                                result = GetParsingError(startTime, in buffer, "Error during parsing/validation: incomplete request.");
                                shouldExit = true;
                            }

                            if (!advanced)
                            {
                                reader.AdvanceTo(buffer.Start, buffer.End);
                                advanced = true;
                            }
                        }
                        catch (BadHttpRequestException e)
                        {
                            Handle(e);
                            shouldExit = true;
                        }
                        catch (ConnectionResetException e)
                        {
                            Handle(e);
                            shouldExit = true;
                        }
                        catch (JsonException ex)
                        {
                            result = GetParsingError(startTime, in buffer, "Error during parsing/validation.", ex);
                            shouldExit = true;
                        }
                    }

                    if (result.HasValue)
                    {
                        await WriteSingleEntryAsync(result.Value, sink, cancellationToken);
                    }

                    shouldExit |= isCompleted && buffer.IsEmpty;
                }
                finally
                {
                    if (!advanced)
                    {
                        reader.AdvanceTo(buffer.Start, buffer.End);
                    }
                }
            }
        }
        finally
        {
            pendingSingleDocument?.Dispose();
            await reader.CompleteAsync();
            if (timeoutSource is not null)
                JsonRpcConfigExtension.ReturnTimeoutCancellationToken(timeoutSource);
        }
    }

    private async ValueTask ProcessSingleDocumentMemoryToSink(
        ReadOnlyMemory<byte> requestBody,
        JsonRpcContext context,
        IJsonRpcResponseSink sink,
        JsonRpcProcessingOptions options,
        CancellationToken cancellationToken)
    {
        long startTime = Stopwatch.GetTimestamp();
        try
        {
            if (TryReadSingleObjectRequest(requestBody, out JsonRpcRequest? directRequest, out JsonDocument? directParamsDocument))
            {
                await ProcessSingleRequestToSink(directRequest, directParamsDocument, context, sink, cancellationToken);
                return;
            }

            if (await TryProcessBatchRequestDirectly(requestBody, context, sink, cancellationToken))
            {
                return;
            }

            await ProcessSingleDocumentMemoryFallbackToSink(requestBody, context, sink, options, startTime, cancellationToken);
        }
        catch (JsonException ex)
        {
            await WriteParsingErrorAsync(new ReadOnlySequence<byte>(requestBody), sink, startTime, "Error during parsing/validation.", cancellationToken, ex);
        }
    }

    private async ValueTask ProcessSingleDocumentMemoryFallbackToSink(
        ReadOnlyMemory<byte> requestBody,
        JsonRpcContext context,
        IJsonRpcResponseSink sink,
        JsonRpcProcessingOptions options,
        long startTime,
        CancellationToken cancellationToken)
    {
        ReadOnlySequence<byte> buffer = new ReadOnlySequence<byte>(requestBody).TrimStart();
        if (buffer.IsEmpty)
        {
            return;
        }

        JsonRpcProcessingOptions singleDocumentOptions = new(JsonRpcInputMode.SingleDocument);
        JsonReaderState readerState = CreateJsonReaderState(singleDocumentOptions);
        JsonDocument? jsonDocument = null;
        try
        {
            bool parsed = TryParseJson(
                ref buffer,
                isFinalBlock: true,
                ref readerState,
                out jsonDocument,
                singleDocumentOptions);

            if (parsed)
            {
                ReadOnlySequence<byte> trailingBuffer = buffer.TrimStart();
                if (!trailingBuffer.IsEmpty)
                {
                    jsonDocument.Dispose();
                    await WriteParsingErrorAsync(trailingBuffer, sink, startTime, "Error during parsing/validation: trailing data after JSON-RPC request.", cancellationToken);
                    return;
                }

                await ProcessJsonDocumentToSink(jsonDocument, context, sink, options, startTime, cancellationToken);
                return;
            }

            if (!buffer.IsEmpty)
            {
                await WriteParsingErrorAsync(buffer, sink, startTime, "Error during parsing/validation: incomplete request.", cancellationToken);
            }
        }
        catch (JsonException ex)
        {
            jsonDocument?.Dispose();
            await WriteParsingErrorAsync(buffer, sink, startTime, "Error during parsing/validation.", cancellationToken, ex);
        }
    }

    private static bool TryReadSingleObjectRequest(
        ReadOnlyMemory<byte> memory,
        [NotNullWhen(true)] out JsonRpcRequest? request,
        out JsonDocument? paramsDocument)
    {
        request = null;
        paramsDocument = null;

        return TryGetSingleDocumentBody(memory, JsonTokenType.StartObject, out ReadOnlyMemory<byte> objectBody)
            && TryReadObjectRequest(objectBody, out request, out paramsDocument);
    }

    private static bool TryGetSingleDocumentBody(
        ReadOnlyMemory<byte> memory,
        JsonTokenType expectedRootToken,
        out ReadOnlyMemory<byte> documentBody)
    {
        documentBody = default;

        ReadOnlyMemory<byte> body = memory[CountLeadingJsonWhitespace(memory.Span)..];
        if (body.IsEmpty)
        {
            return false;
        }

        Utf8JsonReader reader = new(body.Span, isFinalBlock: true, state: default);
        if (!reader.Read() || reader.TokenType != expectedRootToken)
        {
            return false;
        }

        reader.Skip();
        int documentLength = checked((int)reader.BytesConsumed);
        if (HasNonWhitespace(body.Span[documentLength..]))
        {
            return false;
        }

        documentBody = body[..documentLength];
        return true;
    }

    private static bool TryReadObjectRequest(
        ReadOnlyMemory<byte> objectBody,
        [NotNullWhen(true)] out JsonRpcRequest? request,
        out JsonDocument? paramsDocument)
    {
        request = null;
        paramsDocument = null;

        JsonRpcEnvelopeReader envelopeReader = new(objectBody.Span);
        if (!envelopeReader.TryRead(out JsonRpcEnvelope envelope))
        {
            return false;
        }

        ReadOnlyMemory<byte> paramsUtf8 = envelope.HasParams
            ? objectBody.Slice(envelope.ParamsStart, envelope.ParamsLength)
            : default;

        request = CreateRequest(envelope, paramsElement: default, paramsUtf8);
        return true;
    }

    private static JsonRpcRequest CreateRequest(JsonRpcEnvelope envelope, JsonElement paramsElement, ReadOnlyMemory<byte> paramsUtf8) =>
        new()
        {
            JsonRpc = envelope.JsonRpc!,
            Id = envelope.Id,
            Method = envelope.Method!,
            Params = paramsElement,
            ParamsUtf8 = paramsUtf8,
            ParamsKind = envelope.HasParams ? envelope.ParamsKind : JsonValueKind.Undefined
        };

    private static JsonRpcRequest CreateRequest(JsonElement element)
    {
        JsonRpcEnvelope envelope = JsonRpcEnvelopeReader.Read(element, out JsonElement paramsElement);
        return CreateRequest(envelope, paramsElement, paramsUtf8: default);
    }

    private static int CountLeadingJsonWhitespace(ReadOnlySpan<byte> span)
    {
        if (span.IsEmpty || !IsJsonWhitespace(span[0]))
        {
            return 0;
        }

        if (span.Length == 1)
        {
            return 1;
        }

        int index = span.IndexOfAnyExcept(JsonWhitespace);
        return index >= 0 ? index : span.Length;
    }

    private static bool HasNonWhitespace(ReadOnlySpan<byte> span)
    {
        if (span.IsEmpty)
        {
            return false;
        }

        if (!IsJsonWhitespace(span[0]))
        {
            return true;
        }

        if (span.Length == 1)
        {
            return false;
        }

        return span.IndexOfAnyExcept(JsonWhitespace) >= 0;
    }

    private static bool IsJsonWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    private void Handle(ConnectionResetException e)
    {
        if (_logger.IsTrace) _logger.Trace($"Connection reset.{Environment.NewLine}{e}");
    }

    private void Handle(BadHttpRequestException e)
    {
        Metrics.JsonRpcRequestDeserializationFailures++;
        if (_logger.IsDebug) _logger.Debug($"Couldn't read request.{Environment.NewLine}{e}");
    }

    private static bool TryParseJson(
        ref ReadOnlySequence<byte> buffer,
        bool isFinalBlock,
        ref JsonReaderState readerState,
        [NotNullWhen(true)] out JsonDocument? jsonDocument,
        JsonRpcProcessingOptions options)
    {
        Utf8JsonReader jsonReader = new(buffer, isFinalBlock, readerState);
        bool parsed = JsonDocument.TryParseValue(ref jsonReader, out jsonDocument);
        buffer = buffer.Slice(jsonReader.BytesConsumed);
        readerState = parsed
            ? CreateJsonReaderState(options) // Reset state for the next document
            : jsonReader.CurrentState; // Preserve state for resumption when more data arrives

        return parsed;
    }

    private static JsonReaderState CreateJsonReaderState(JsonRpcProcessingOptions options) =>
        new(options.InputMode == JsonRpcInputMode.MultipleDocuments ? _socketJsonReaderOptions : default);

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
                    JsonRpcRequest request = CreateRequest(rootElement);
                    if (_logger.IsDebug) _logger.Debug($"JSON RPC request {request.Method}");

                    JsonRpcResult.Entry singleResponse = await HandleSingleRequest(request, context);
                    await WriteSingleEntryAsync(singleResponse, sink, cancellationToken);
                    break;

                case JsonValueKind.Array:
                    await ProcessBatchDocumentToSink(rootElement, context, sink, cancellationToken);
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
        JsonDocument? paramsDocument,
        JsonRpcContext context,
        IJsonRpcResponseSink sink,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_logger.IsDebug) _logger.Debug($"JSON RPC request {request.Method}");

            JsonRpcResult.Entry response = await HandleSingleRequest(request, context);
            await WriteSingleEntryAsync(response, sink, cancellationToken);
        }
        finally
        {
            request.DisposeParsedParamsDocument();
            paramsDocument?.Dispose();
        }
    }

    private async ValueTask ProcessBatchDocumentToSink(
        JsonElement rootElement,
        JsonRpcContext context,
        IJsonRpcResponseSink sink,
        CancellationToken cancellationToken)
    {
        int requestCount = rootElement.GetArrayLength();
        if (_logger.IsDebug) _logger.Debug($"{requestCount} JSON RPC requests");

        if (!context.IsAuthenticated && requestCount > _jsonRpcConfig.MaxBatchSize)
        {
            await WriteBatchSizeLimitErrorAsync(requestCount, sink, cancellationToken);
            return;
        }

        await sink.BeginBatchAsync(cancellationToken);
        long startTime = Stopwatch.GetTimestamp();
        int requestIndex = 0;
        bool isStopped = false;
        try
        {
            foreach (JsonElement item in rootElement.EnumerateArray())
            {
                JsonRpcRequest jsonRpcRequest = CreateRequest(item);
                JsonRpcResult.Entry response = isStopped
                    ? CreateBatchResponseLimitEntry(jsonRpcRequest)
                    : await HandleSingleRequest(jsonRpcRequest, context);

                if (_logger.IsTrace) _logger.Trace($"  {++requestIndex}/{requestCount} JSON RPC request - {jsonRpcRequest} handled after {response.Report.HandlingTimeMicroseconds}");
                if (_logger.IsTrace) TraceResult(response);

                await WriteBatchEntryAsync(response, sink, cancellationToken);
                isStopped |= sink.StopRequested;
            }

            if (_logger.IsTrace) _logger.Trace($"  {requestCount} requests handled in {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds:N0}ms");
        }
        finally
        {
            await sink.EndBatchAsync(cancellationToken);
        }
    }

    private async ValueTask<bool> TryProcessBatchRequestDirectly(
        ReadOnlyMemory<byte> memory,
        JsonRpcContext context,
        IJsonRpcResponseSink sink,
        CancellationToken cancellationToken)
    {
        if (!TryGetSingleDocumentBody(memory, JsonTokenType.StartArray, out ReadOnlyMemory<byte> batchBody))
        {
            return false;
        }

        await ProcessBatchMemoryToSink(batchBody, context, sink, cancellationToken);
        return true;
    }

    private async ValueTask ProcessBatchMemoryToSink(
        ReadOnlyMemory<byte> batchBody,
        JsonRpcContext context,
        IJsonRpcResponseSink sink,
        CancellationToken cancellationToken)
    {
        int? requestCount = null;
        if (!context.IsAuthenticated)
        {
            requestCount = JsonRpcArrayReader.CountItems(batchBody);
            if (requestCount > _jsonRpcConfig.MaxBatchSize)
            {
                await WriteBatchSizeLimitErrorAsync(requestCount.Value, sink, cancellationToken);
                return;
            }
        }

        if (_logger.IsDebug)
        {
            _logger.Debug(requestCount is null ? "JSON RPC batch request" : $"{requestCount} JSON RPC requests");
        }

        await sink.BeginBatchAsync(cancellationToken);
        long startTime = Stopwatch.GetTimestamp();
        int requestIndex = 0;
        bool isStopped = false;
        JsonReaderState readerState = default;
        int offset = 0;
        bool started = false;
        List<JsonDocument>? requestDocuments = null;
        List<JsonRpcRequest>? requestsWithRawParams = null;

        try
        {
            while (JsonRpcArrayReader.TryReadNextItem(batchBody, ref offset, ref readerState, ref started, out ReadOnlyMemory<byte> itemBody))
            {
                requestIndex++;
                JsonRpcRequest jsonRpcRequest = DeserializeBatchItem(itemBody, out JsonDocument? requestDocument);
                if (requestDocument is not null)
                {
                    requestDocuments ??= [];
                    requestDocuments.Add(requestDocument);
                }
                else if (!jsonRpcRequest.ParamsUtf8.IsEmpty)
                {
                    requestsWithRawParams ??= [];
                    requestsWithRawParams.Add(jsonRpcRequest);
                }

                JsonRpcResult.Entry response = isStopped
                    ? CreateBatchResponseLimitEntry(jsonRpcRequest)
                    : await HandleSingleRequest(jsonRpcRequest, context);

                if (_logger.IsTrace)
                {
                    string progress = requestCount is null ? requestIndex.ToString() : $"{requestIndex}/{requestCount}";
                    _logger.Trace($"  {progress} JSON RPC request - {jsonRpcRequest} handled after {response.Report.HandlingTimeMicroseconds}");
                    TraceResult(response);
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
                if (requestDocuments is not null)
                {
                    foreach (JsonDocument requestDocument in requestDocuments)
                    {
                        requestDocument.Dispose();
                    }
                }

                if (requestsWithRawParams is not null)
                {
                    foreach (JsonRpcRequest request in requestsWithRawParams)
                    {
                        request.DisposeParsedParamsDocument();
                    }
                }
            }
        }
    }

    private JsonRpcRequest DeserializeBatchItem(ReadOnlyMemory<byte> itemBody, out JsonDocument? requestDocument)
    {
        if (TryReadObjectRequest(itemBody, out JsonRpcRequest? directRequest, out JsonDocument? paramsDocument))
        {
            requestDocument = paramsDocument;
            return directRequest;
        }

        requestDocument = JsonDocument.Parse(itemBody);
        try
        {
            return CreateRequest(requestDocument.RootElement);
        }
        catch
        {
            requestDocument.Dispose();
            requestDocument = null;
            throw;
        }
    }

    private async ValueTask WriteInvalidRequestAsync(
        IJsonRpcResponseSink sink,
        long startTime,
        CancellationToken cancellationToken)
    {
        Metrics.JsonRpcInvalidRequests++;
        JsonRpcErrorResponse invalidResponse = _jsonRpcService.GetErrorResponse(ErrorCodes.InvalidRequest, "Invalid request");

        if (_logger.IsTrace)
        {
            TraceResult(invalidResponse);
            _logger.Trace($"  Failed request handled in {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds:N0}ms");
        }

        JsonRpcResult.Entry result = new(invalidResponse, new RpcReport("# parsing error #", (long)Stopwatch.GetElapsedTime(startTime).TotalMicroseconds, false));
        await WriteSingleEntryAsync(result, sink, cancellationToken);
    }

    private async ValueTask WriteShutdownResponseAsync(IJsonRpcResponseSink sink, CancellationToken cancellationToken)
    {
        JsonRpcErrorResponse response = _jsonRpcService.GetErrorResponse(ErrorCodes.ResourceUnavailable, "Shutting down");
        using JsonRpcResult.Entry entry = RecordResponse(response, new RpcReport("Shutdown", 0, false));
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
                jsonRpcRequest.Id,
                $"{nameof(IJsonRpcConfig.MaxBatchResponseBodySize)} of {_jsonRpcConfig.MaxBatchResponseBodySize / 1.KB}KB exceeded"),
            RpcReport.Error);

    private async ValueTask WriteParsingErrorAsync(
        ReadOnlySequence<byte> buffer,
        IJsonRpcResponseSink sink,
        long startTime,
        string error,
        CancellationToken cancellationToken,
        Exception? exception = null)
    {
        JsonRpcResult.Entry result = GetParsingError(startTime, in buffer, error, exception);
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
            JsonRpcResult.Entry recorded = RecordResponse(entry);
            ValueTask writeTask = isBatch
                ? sink.WriteBatchItemAsync(recorded.Response, recorded.Report, cancellationToken)
                : sink.WriteSingleAsync(recorded.Response, recorded.Report, cancellationToken);
            if (writeTask.IsCompletedSuccessfully)
            {
                writeTask.GetAwaiter().GetResult();
                DisposeEntry(entry);
                return ValueTask.CompletedTask;
            }

            return AwaitAndDisposeAsync(writeTask, entry);
        }
        catch
        {
            DisposeEntry(entry);
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
            DisposeEntry(entry);
        }
    }

    private static void DisposeEntry(JsonRpcResult.Entry entry) => entry.Dispose();

    private JsonRpcResult.Entry GetParsingError(long startTime, ref readonly ReadOnlySequence<byte> buffer, string error, Exception? exception = null)
    {
        Metrics.JsonRpcRequestDeserializationFailures++;
        if (_logger.IsError) _logger.Error(error, exception);

        if (_logger.IsDebug)
        {
            // Attempt to get and log the request body from the bytes buffer if Debug logging is enabled
            const int sliceSize = 1000;
            if (Encoding.UTF8.TryGetStringSlice(in buffer, sliceSize, out bool isFullString, out string data))
            {
                error = isFullString
                    ? $"{error} Data:\n{data}\n"
                    : $"{error} Data (first {sliceSize} chars):\n{data[..sliceSize]}\n";

                _logger.Debug(error);
            }
        }

        JsonRpcErrorResponse response = _jsonRpcService.GetErrorResponse(ErrorCodes.ParseError, "parse error");
        if (_logger.IsTrace) TraceResult(response);
        return RecordResponse(response, new RpcReport("# parsing error #", (long)Stopwatch.GetElapsedTime(startTime).TotalMicroseconds, false));
    }

    private ValueTask<JsonRpcResult.Entry> HandleSingleRequest(JsonRpcRequest request, JsonRpcContext context)
    {
        Metrics.JsonRpcRequests++;
        long startTime = Stopwatch.GetTimestamp();

        ValueTask<JsonRpcResponse> responseTask = _jsonRpcService.SendRequestAsync(request, context);
        if (responseTask.IsCompletedSuccessfully)
        {
            return ValueTask.FromResult(CreateSingleRequestEntry(request, responseTask.Result, startTime));
        }

        return AwaitAndCreateEntryAsync(responseTask, request, startTime);

        async ValueTask<JsonRpcResult.Entry> AwaitAndCreateEntryAsync(
            ValueTask<JsonRpcResponse> responseTask,
            JsonRpcRequest request,
            long startTime)
        {
            JsonRpcResponse response = await responseTask;
            return CreateSingleRequestEntry(request, response, startTime);
        }
    }

    private JsonRpcResult.Entry CreateSingleRequestEntry(JsonRpcRequest request, JsonRpcResponse response, long startTime)
    {
        bool isError = response.TryGetError(out Error? responseError);
        bool isSuccess = !isError;
        if (!isSuccess)
        {
            if (responseError?.SuppressWarning == false)
            {
                if (_logger.IsWarn) _logger.Warn($"Error response handling JsonRpc Id:{request.Id} Method:{request.Method} | Code: {responseError.Code} Message: {responseError.Message}");
                if (_logger.IsTrace) _logger.Trace($"Error when handling {request} | {SerializeResponseForDiagnostics(response)}");
            }
            Metrics.JsonRpcErrors++;
        }
        else
        {
            if (_logger.IsTrace) _logger.Trace($"Responded to Id:{request.Id} Method:{request.Method} in {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds:N0}ms");
            Metrics.JsonRpcSuccesses++;
        }

        string reportMethod = responseError?.Code == ErrorCodes.MethodNotFound
            ? RpcReport.UnknownMethod
            : request.Method;
        JsonRpcResult.Entry result = new(
            response,
            new RpcReport(reportMethod, (long)Stopwatch.GetElapsedTime(startTime).TotalMicroseconds, isSuccess));

        if (_logger.IsTrace) TraceResult(result);
        return result;
    }

    private bool IsRecordingRequest => (_jsonRpcConfig.RpcRecorderState & RpcRecorderState.Request) != 0;
    private bool IsRecordingResponse => (_jsonRpcConfig.RpcRecorderState & RpcRecorderState.Response) != 0;

    private JsonRpcResult.Entry RecordResponse(JsonRpcResponse response, in RpcReport report)
    {
        JsonRpcResult.Entry result = new(response, report);
        return RecordResponse(result);
    }

    private JsonRpcResult.Entry RecordResponse(in JsonRpcResult.Entry result) =>
        !IsRecordingResponse ? result : RecordResponseSlow(result);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsonRpcResult.Entry RecordResponseSlow(in JsonRpcResult.Entry result)
    {
        _recorder.RecordResponse(SerializeForDiagnostics(result));
        return result;
    }

    private static readonly StreamPipeReaderOptions _pipeReaderOptions = new(leaveOpen: false);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RecordRequest(ReadOnlyMemory<byte> requestBody) =>
        _recorder.RecordRequest(Encoding.UTF8.GetString(requestBody.Span));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async ValueTask<PipeReader> RecordRequest(PipeReader reader)
    {
        if (!IsRecordingRequest)
        {
            return reader;
        }

        Stream memoryStream = RecyclableStream.GetStream("recorder");
        await reader.CopyToAsync(memoryStream);
        memoryStream.Seek(0, SeekOrigin.Begin);

        StreamReader streamReader = new(memoryStream);

        string requestString = await streamReader.ReadToEndAsync();
        _recorder.RecordRequest(requestString);

        memoryStream.Seek(0, SeekOrigin.Begin);
        return PipeReader.Create(memoryStream, _pipeReaderOptions);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void TraceResult(in JsonRpcResult.Entry response)
    {
        if (_logger.IsTrace)
        {
            string json = SerializeForDiagnostics(response);

            _logger.Trace($"Sending JSON RPC response: {json}");
        }
    }

    private static string SerializeForDiagnostics(in JsonRpcResult.Entry response)
    {
        JsonRpcResponse responseToSerialize = TryGetDiagnosticResponse(response.Response, out JsonRpcResponse? diagnosticResponse)
            ? diagnosticResponse
            : response.Response;

        ArrayBufferWriter<byte> writer = new();
        JsonRpcResponseWriter.Write(writer, responseToSerialize, EthereumJsonSerializer.JsonOptionsIndented);
        using JsonDocument document = JsonDocument.Parse(writer.WrittenMemory);
        return JsonSerializer.Serialize(new DiagnosticJsonRpcResult(document.RootElement, response.Report), EthereumJsonSerializer.JsonOptionsIndented);
    }

    private static string SerializeResponseForDiagnostics(JsonRpcResponse response)
    {
        ArrayBufferWriter<byte> writer = new();
        JsonRpcResponseWriter.Write(writer, response, EthereumJsonSerializer.JsonOptionsIndented);
        return Encoding.UTF8.GetString(writer.WrittenSpan);
    }

    private readonly record struct DiagnosticJsonRpcResult(JsonElement Response, RpcReport Report);

    private static bool TryGetDiagnosticResponse(JsonRpcResponse response, [NotNullWhen(true)] out JsonRpcResponse? diagnosticResponse)
    {
        diagnosticResponse = response switch
        {
            _ when response.TryGetStreamableResult(out _) => new JsonRpcSuccessResponse
            {
                Id = response.Id,
                Result = "# streamable response omitted #"
            },
            JsonRpcErrorResponse { Error.Data: IStreamableResult } errorResponse => new JsonRpcErrorResponse
            {
                Id = errorResponse.Id,
                Error = new Error
                {
                    Code = errorResponse.Error.Code,
                    Message = errorResponse.Error.Message,
                    Data = "# streamable error data omitted #",
                    SuppressWarning = errorResponse.Error.SuppressWarning
                }
            },
            _ => null
        };

        return diagnosticResponse is not null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void TraceResult(JsonRpcErrorResponse response)
    {
        if (_logger.IsTrace)
        {
            string json = SerializeResponseForDiagnostics(response);

            _logger.Trace($"Sending JSON RPC response: {json}");
        }
    }
}
