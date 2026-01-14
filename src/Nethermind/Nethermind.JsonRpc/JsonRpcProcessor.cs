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
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Nethermind.Config;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Resettables;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc;

public class JsonRpcProcessor : IJsonRpcProcessor
{
    private readonly IJsonRpcConfig _jsonRpcConfig;
    private readonly ILogger _logger;
    private readonly IJsonRpcService _jsonRpcService;
    private readonly Recorder _recorder;
    private readonly IProcessExitSource? _processExitSource;

    public JsonRpcProcessor(IJsonRpcService jsonRpcService, IJsonRpcConfig jsonRpcConfig, IFileSystem fileSystem, ILogManager logManager, IProcessExitSource? processExitSource = null)
    {
        _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
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

    public CancellationToken ProcessExit
        => _processExitSource?.Token ?? default;

    private (JsonRpcRequest? Model, ArrayPoolList<JsonRpcRequest>? Collection) DeserializeObjectOrArray(JsonDocument doc)
    {
        return doc.RootElement.ValueKind switch
        {
            JsonValueKind.Array => (null, DeserializeArray(doc.RootElement)),
            JsonValueKind.Object => (DeserializeObject(doc.RootElement), null),
            _ => ThrowInvalid()
        };

        [DoesNotReturn, StackTraceHidden]
        static (JsonRpcRequest? Model, ArrayPoolList<JsonRpcRequest>? Collection) ThrowInvalid()
            => throw new JsonException("Invalid");
    }

    private JsonRpcRequest DeserializeObject(JsonElement element)
    {
        string? jsonRpc = null;
        if (element.TryGetProperty("jsonrpc"u8, out JsonElement versionElement))
        {
            if (versionElement.ValueEquals("2.0"u8))
            {
                jsonRpc = "2.0";
            }
        }

        object? id = null;
        if (element.TryGetProperty("id"u8, out JsonElement idElement))
        {
            if (idElement.ValueKind == JsonValueKind.Number)
            {
                if (idElement.TryGetInt64(out long idNumber))
                {
                    id = idNumber;
                }
                else if (decimal.TryParse(idElement.GetRawText(), out var value))
                {
                    id = value;
                }
            }
            else
            {
                id = idElement.GetString();
            }
        }

        string? method = null;
        if (element.TryGetProperty("method"u8, out JsonElement methodElement))
        {
            method = methodElement.GetString();
        }

        if (!element.TryGetProperty("params"u8, out JsonElement paramsElement))
        {
            paramsElement = default;
        }

        return new JsonRpcRequest
        {
            JsonRpc = jsonRpc!,
            Id = id!,
            Method = method!,
            Params = paramsElement
        };
    }

    private ArrayPoolList<JsonRpcRequest> DeserializeArray(JsonElement element) =>
        new(element.GetArrayLength(), element.EnumerateArray().Select(DeserializeObject));

    private static readonly JsonReaderOptions _jsonReaderOptions = new() { AllowMultipleValues = true };

    public async IAsyncEnumerable<JsonRpcResult> ProcessAsync(PipeReader reader, JsonRpcContext context)
    {
        if (ProcessExit.IsCancellationRequested)
        {
            JsonRpcErrorResponse response = _jsonRpcService.GetErrorResponse(ErrorCodes.ResourceUnavailable, "Shutting down");
            yield return JsonRpcResult.Single(RecordResponse(response, new RpcReport("Shutdown", 0, false)));
        }

        if (IsRecordingRequest)
        {
            reader = await RecordRequest(reader);
        }

        using CancellationTokenSource timeoutSource = _jsonRpcConfig.BuildTimeoutCancellationToken();
        JsonReaderState readerState = new(_jsonReaderOptions);
        bool shouldExit = false;
        try
        {
            while (!shouldExit)
            {
                long startTime = Stopwatch.GetTimestamp();
                ReadResult readResult = await reader.ReadAsync(timeoutSource.Token);
                ReadOnlySequence<byte> buffer = readResult.Buffer;

                try
                {
                    bool isCompleted = readResult.IsCompleted || readResult.IsCanceled;
                    JsonRpcResult? result = null;
                    buffer = buffer.TrimStart();
                    if (!buffer.IsEmpty)
                    {
                        try
                        {
                            if (TryParseJson(ref buffer, isCompleted, ref readerState, out JsonDocument? jsonDocument))
                            {
                                result = await ProcessJsonDocument(jsonDocument, context, startTime);
                            }
                            else if (isCompleted && !buffer.IsEmpty)
                            {
                                result = GetParsingError(startTime, in buffer, "Error during parsing/validation: incomplete request.");
                                shouldExit = true;
                            }
                        }
                        catch (BadHttpRequestException e)
                        {
                            Metrics.JsonRpcRequestDeserializationFailures++;
                            if (_logger.IsDebug) _logger.Debug($"Couldn't read request.{Environment.NewLine}{e}");
                            shouldExit = true;
                        }
                        catch (ConnectionResetException e)
                        {
                            if (_logger.IsTrace) _logger.Trace($"Connection reset.{Environment.NewLine}{e}");
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
                        yield return result.Value;
                    }

                    shouldExit |= isCompleted && buffer.IsEmpty;
                }
                finally
                {
                    reader.AdvanceTo(buffer.Start, buffer.End);
                }
            }
        }
        finally
        {
            await reader.CompleteAsync();
        }
    }

    private static bool TryParseJson(ref ReadOnlySequence<byte> buffer, bool isFinalBlock, ref JsonReaderState readerState, [NotNullWhen(true)] out JsonDocument? jsonDocument)
    {
        Utf8JsonReader jsonReader = new(buffer, isFinalBlock, readerState);
        if (!JsonDocument.TryParseValue(ref jsonReader, out jsonDocument))
        {
            readerState = jsonReader.CurrentState; // Preserve state for resumption when more data arrives
            return false;
        }

        buffer = buffer.Slice(jsonReader.BytesConsumed);
        readerState = new(_jsonReaderOptions); // Reset state for the next document
        return true;
    }

    private async Task<JsonRpcResult?> ProcessJsonDocument(JsonDocument jsonDocument, JsonRpcContext context, long startTime)
    {
        try
        {
            (JsonRpcRequest? model, ArrayPoolList<JsonRpcRequest>? collection) = DeserializeObjectOrArray(jsonDocument);

            // Handles a single JSON RPC request
            if (model is not null)
            {
                if (_logger.IsDebug) _logger.Debug($"JSON RPC request {model.Method}");

                JsonRpcResult.Entry result = await HandleSingleRequest(model, context);
                result.Response.AddDisposable(jsonDocument.Dispose);

                return JsonRpcResult.Single(RecordResponse(result));
            }

            // Processes a collection of JSON RPC requests
            if (collection is not null)
            {
                if (_logger.IsDebug) _logger.Debug($"{collection.Count} JSON RPC requests");

                if (!context.IsAuthenticated && collection.Count > _jsonRpcConfig.MaxBatchSize)
                {
                    if (_logger.IsWarn) _logger.Warn($"The batch size limit was exceeded. The requested batch size {collection.Count}, and the current config setting is JsonRpc.{nameof(_jsonRpcConfig.MaxBatchSize)} = {_jsonRpcConfig.MaxBatchSize}.");
                    JsonRpcErrorResponse? errorResponse = _jsonRpcService.GetErrorResponse(ErrorCodes.LimitExceeded, "Batch size limit exceeded");
                    errorResponse.AddDisposable(jsonDocument.Dispose);

                    collection.Dispose();
                    return JsonRpcResult.Single(RecordResponse(errorResponse, RpcReport.Error));
                }
                JsonRpcBatchResult jsonRpcBatchResult = new((e, c) => IterateRequest(collection, context, e).GetAsyncEnumerator(c));
                jsonRpcBatchResult.AddDisposable(jsonDocument.Dispose);
                jsonRpcBatchResult.AddDisposable(collection.Dispose);
                return JsonRpcResult.Collection(jsonRpcBatchResult);
            }

            // Handles invalid requests (neither object nor array)
            Metrics.JsonRpcInvalidRequests++;
            JsonRpcErrorResponse invalidResponse = _jsonRpcService.GetErrorResponse(ErrorCodes.InvalidRequest, "Invalid request");
            invalidResponse.AddDisposable(jsonDocument.Dispose);

            if (_logger.IsTrace)
            {
                TraceResult(invalidResponse);
                _logger.Trace($"  Failed request handled in {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds:N0}ms");
            }
            return JsonRpcResult.Single(RecordResponse(invalidResponse, new RpcReport("# parsing error #", (long)Stopwatch.GetElapsedTime(startTime).TotalMilliseconds, false)));
        }
        catch
        {
            jsonDocument.Dispose();
            throw;
        }
    }

    private JsonRpcResult GetParsingError(long startTime, ref readonly ReadOnlySequence<byte> buffer, string error, Exception? exception = null)
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
        return JsonRpcResult.Single(RecordResponse(response, new RpcReport("# parsing error #", (long)Stopwatch.GetElapsedTime(startTime).TotalMicroseconds, false)));
    }

    private async IAsyncEnumerable<JsonRpcResult.Entry> IterateRequest(
        ArrayPoolList<JsonRpcRequest> requests,
        JsonRpcContext context,
        JsonRpcBatchResultAsyncEnumerator enumerator)
    {
        try
        {
            long startTime = Stopwatch.GetTimestamp();
            int requestIndex = 0;
            for (int index = 0; index < requests.Count; index++)
            {
                JsonRpcRequest jsonRpcRequest = requests[index];

                JsonRpcResult.Entry response = enumerator.IsStopped
                    ? new JsonRpcResult.Entry(
                        _jsonRpcService.GetErrorResponse(
                            ErrorCodes.LimitExceeded,
                            jsonRpcRequest.Method,
                            jsonRpcRequest.Id,
                            $"{nameof(IJsonRpcConfig.MaxBatchResponseBodySize)} of {_jsonRpcConfig.MaxBatchResponseBodySize / 1.KB()}KB exceeded"),
                        RpcReport.Error)
                    : await HandleSingleRequest(jsonRpcRequest, context);

                if (_logger.IsTrace) _logger.Trace($"  {++requestIndex}/{requests.Count} JSON RPC request - {jsonRpcRequest} handled after {response.Report.HandlingTimeMicroseconds}");
                if (_logger.IsTrace) TraceResult(response);
                yield return !IsRecordingResponse ? response : RecordResponse(response);
            }

            if (_logger.IsTrace) _logger.Trace($"  {requests.Count} requests handled in {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds:N0}ms");
        }
        finally
        {
            requests.Dispose();
        }
    }

    private async Task<JsonRpcResult.Entry> HandleSingleRequest(JsonRpcRequest request, JsonRpcContext context)
    {
        Metrics.JsonRpcRequests++;
        long startTime = Stopwatch.GetTimestamp();

        JsonRpcResponse response = await _jsonRpcService.SendRequestAsync(request, context);
        JsonRpcErrorResponse localErrorResponse = response as JsonRpcErrorResponse;
        bool isSuccess = localErrorResponse is null;
        if (!isSuccess)
        {
            if (localErrorResponse?.Error?.SuppressWarning == false)
            {
                if (_logger.IsWarn) _logger.Warn($"Error response handling JsonRpc Id:{request.Id} Method:{request.Method} | Code: {localErrorResponse.Error.Code} Message: {localErrorResponse.Error.Message}");
                if (_logger.IsTrace) _logger.Trace($"Error when handling {request} | {JsonSerializer.Serialize(localErrorResponse, EthereumJsonSerializer.JsonOptionsIndented)}");
            }
            Metrics.JsonRpcErrors++;
        }
        else
        {
            if (_logger.IsTrace) _logger.Trace($"Responded to Id:{request.Id} Method:{request.Method} in {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds:N0}ms");
            Metrics.JsonRpcSuccesses++;
        }

        JsonRpcResult.Entry result = new(response, new RpcReport(request.Method, (long)Stopwatch.GetElapsedTime(startTime).TotalMicroseconds, isSuccess));

        if (_logger.IsTrace) TraceResult(result);
        return result;
    }

    private bool IsRecordingRequest => (_jsonRpcConfig.RpcRecorderState & RpcRecorderState.Request) != 0;
    private bool IsRecordingResponse => (_jsonRpcConfig.RpcRecorderState & RpcRecorderState.Response) != 0;

    private JsonRpcResult.Entry RecordResponse(JsonRpcResponse response, in RpcReport report)
    {
        JsonRpcResult.Entry result = new(response, report);
        return !IsRecordingResponse ? result : RecordResponse(result);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsonRpcResult.Entry RecordResponse(in JsonRpcResult.Entry result)
    {
        if (IsRecordingResponse)
        {
            _recorder.RecordResponse(JsonSerializer.Serialize(result, EthereumJsonSerializer.JsonOptionsIndented));
        }

        return result;
    }

    private static readonly StreamPipeReaderOptions _pipeReaderOptions = new StreamPipeReaderOptions(leaveOpen: false);

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
            string json = JsonSerializer.Serialize(response, EthereumJsonSerializer.JsonOptionsIndented);

            _logger.Trace($"Sending JSON RPC response: {json}");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void TraceResult(JsonRpcErrorResponse response)
    {
        if (_logger.IsTrace)
        {
            string json = JsonSerializer.Serialize(response, EthereumJsonSerializer.JsonOptionsIndented);

            _logger.Trace($"Sending JSON RPC response: {json}");
        }
    }
}
