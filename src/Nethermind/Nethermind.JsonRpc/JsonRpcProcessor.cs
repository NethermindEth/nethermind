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
using Nethermind.Core.Utils;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;
using JsonException = System.Text.Json.JsonException;
using JsonSerializer = System.Text.Json.JsonSerializer;

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
        JsonValueKind type = doc.RootElement.ValueKind;
        if (type == JsonValueKind.Array)
        {
            return (null, DeserializeArray(doc.RootElement));
        }
        else if (type == JsonValueKind.Object)
        {
            return (DeserializeObject(doc.RootElement), null);
        }
        else
        {
            throw new JsonException("Invalid");
        }
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

    public async IAsyncEnumerable<JsonRpcResult> ProcessAsync(PipeReader reader, JsonRpcContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (ProcessExit.IsCancellationRequested)
        {
            JsonRpcErrorResponse response = _jsonRpcService.GetErrorResponse(ErrorCodes.ResourceUnavailable, "Shutting down");
            yield return JsonRpcResult.Single(RecordResponse(response, new RpcReport("Shutdown", 0, 0, false)));
        }

        reader = await RecordRequest(reader);

        await foreach (JsonParseResult parseResult in JsonRpcUtils.MultiParseJsonDocument(reader, cancellationToken))
        {
            JsonRpcResult? result = await HandleJsonParseResult(parseResult, context, cancellationToken);
            if (result is not null)
            {
                yield return result.Value;
            }
            else
            {
                break;
            }
        }

        // Completes the PipeReader's asynchronous reading operation.
        await reader.CompleteAsync();
    }

    public async Task<JsonRpcResult?> HandleJsonParseResult(JsonParseResult parseResult, JsonRpcContext context, CancellationToken cancellation)
    {
        long startTime = parseResult.StartTime;

        // Handles general exceptions during parsing and validation.
        // Sends an error response and stops the stopwatch.
        JsonRpcResult GetParsingError(ref readonly ReadOnlySequence<byte> buffer, string error, Exception? exception = null)
        {
            Metrics.JsonRpcRequestDeserializationFailures++;

            if (_logger.IsError)
            {
                _logger.Error(error, exception);
            }

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

            JsonRpcErrorResponse response = _jsonRpcService.GetErrorResponse(ErrorCodes.ParseError, "Incorrect message");
            TraceResult(response);
            return JsonRpcResult.Single(RecordResponse(response, new RpcReport("# parsing error #", (long)Stopwatch.GetElapsedTime(startTime).TotalMicroseconds, startTime, false)));
        }
        JsonRpcResult? deserializationFailureResult = null;

        JsonDocument jsonDocument = parseResult.JsonDocument;
        JsonRpcRequest? model = null;
        ArrayPoolList<JsonRpcRequest>? collection = null;
        try
        {
            if (parseResult.Exception is not null)
            {
                deserializationFailureResult = GetParsingError(in parseResult.LastBytes, parseResult.Exception.ToString(), parseResult.Exception);
            }
            else
            {
                // Deserializes the JSON document into a request object or a collection of requests.
                (model, collection) = DeserializeObjectOrArray(jsonDocument);
            }
        }
        catch (BadHttpRequestException e)
        {
            // Increments failure metric and logs the exception, then stops processing.
            Metrics.JsonRpcRequestDeserializationFailures++;
            if (_logger.IsDebug) _logger.Debug($"Couldn't read request.{Environment.NewLine}{e}");
            return null;
        }
        catch (ConnectionResetException e)
        {
            // Logs exception, then stop processing.
            if (_logger.IsTrace) _logger.Trace($"Connection reset.{Environment.NewLine}{e}");
            return null;
        }
        catch (Exception ex)
        {
            deserializationFailureResult = GetParsingError(in ReadOnlySequence<byte>.Empty, "Error during parsing/validation.", ex);
        }

        // Checks for deserialization failure and yields the result.
        if (deserializationFailureResult.HasValue)
        {
            return deserializationFailureResult.Value;
        }

        // Handles a single JSON RPC request.
        if (model is not null)
        {
            if (_logger.IsDebug) _logger.Debug($"JSON RPC request {model}");

            // Processes the individual request.
            JsonRpcResult.Entry result = await HandleSingleRequest(model, context);
            result.Response.AddDisposable(() => jsonDocument.Dispose());

            // Returns the result of the processed request.
            return JsonRpcResult.Single(RecordResponse(result));
        }

        // Processes a collection of JSON RPC requests.
        if (collection is not null)
        {
            if (_logger.IsDebug) _logger.Debug($"{collection.Count} JSON RPC requests");

            // Checks for authentication and batch size limit.
            if (!context.IsAuthenticated && collection.Count > _jsonRpcConfig.MaxBatchSize)
            {
                if (_logger.IsWarn) _logger.Warn($"The batch size limit was exceeded. The requested batch size {collection.Count}, and the current config setting is JsonRpc.{nameof(_jsonRpcConfig.MaxBatchSize)} = {_jsonRpcConfig.MaxBatchSize}.");
                JsonRpcErrorResponse? response = _jsonRpcService.GetErrorResponse(ErrorCodes.LimitExceeded, "Batch size limit exceeded");
                response.AddDisposable(() => jsonDocument.Dispose());

                deserializationFailureResult = JsonRpcResult.Single(RecordResponse(response, RpcReport.Error));
                collection.Dispose();
                return deserializationFailureResult.Value;
            }
            JsonRpcBatchResult jsonRpcBatchResult = new((e, c) => IterateRequest(collection, context, e).GetAsyncEnumerator(c));
            jsonRpcBatchResult.AddDisposable(() => collection.Dispose());
            return JsonRpcResult.Collection(jsonRpcBatchResult);
        }

        // Handles invalid requests.
        if (model is null && collection is null)
        {
            Metrics.JsonRpcInvalidRequests++;
            JsonRpcErrorResponse errorResponse = _jsonRpcService.GetErrorResponse(ErrorCodes.InvalidRequest, "Invalid request");
            errorResponse.AddDisposable(() => jsonDocument.Dispose());

            TraceResult(errorResponse);
            if (_logger.IsDebug) _logger.Debug($"  Failed request handled in {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds:N0}ms");
            deserializationFailureResult = JsonRpcResult.Single(RecordResponse(errorResponse, new RpcReport("# parsing error #", (long)Stopwatch.GetElapsedTime(startTime).TotalMilliseconds, startTime, false)));
            return deserializationFailureResult.Value;
        }

        return null;
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

                if (_logger.IsDebug) _logger.Debug($"  {++requestIndex}/{requests.Count} JSON RPC request - {jsonRpcRequest} handled after {response.Report.HandlingTimeMicroseconds}");
                TraceResult(response);
                yield return RecordResponse(response);
            }

            if (_logger.IsDebug) _logger.Debug($"  {requests.Count} requests handled in {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds:N0}ms");
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
                if (_logger.IsWarn) _logger.Warn($"Error when handling {request} | {JsonSerializer.Serialize(localErrorResponse, EthereumJsonSerializer.JsonOptionsIndented)}");
            }
            Metrics.JsonRpcErrors++;
        }
        else
        {
            if (_logger.IsDebug) _logger.Debug($"Responded to {request}");
            Metrics.JsonRpcSuccesses++;
        }


        if (_logger.IsDebug) _logger.Debug($"  {request} handled in {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds:N0}ms");

        JsonRpcResult.Entry result = new(response, new RpcReport(request.Method, (long)Stopwatch.GetElapsedTime(startTime).TotalMicroseconds, startTime, isSuccess));
        TraceResult(result);
        return result;
    }

    private JsonRpcResult.Entry RecordResponse(JsonRpcResponse response, RpcReport report) =>
        RecordResponse(new JsonRpcResult.Entry(response, report));

    private JsonRpcResult.Entry RecordResponse(JsonRpcResult.Entry result)
    {
        if ((_jsonRpcConfig.RpcRecorderState & RpcRecorderState.Response) != 0)
        {
            _recorder.RecordResponse(JsonSerializer.Serialize(result, EthereumJsonSerializer.JsonOptionsIndented));
        }

        return result;
    }

    private static readonly StreamPipeReaderOptions _pipeReaderOptions = new StreamPipeReaderOptions(leaveOpen: false);

    private async ValueTask<PipeReader> RecordRequest(PipeReader reader)
    {
        if ((_jsonRpcConfig.RpcRecorderState & RpcRecorderState.Request) != 0)
        {
            // WARNING: This will read all request
            Stream memoryStream = RecyclableStream.GetStream("recorder");
            await reader.CopyToAsync(memoryStream);
            memoryStream.Seek(0, SeekOrigin.Begin);

            StreamReader streamReader = new(memoryStream);

            string requestString = await streamReader.ReadToEndAsync();
            _recorder.RecordRequest(requestString);

            memoryStream.Seek(0, SeekOrigin.Begin);
            return PipeReader.Create(memoryStream, _pipeReaderOptions);
        }

        return reader;
    }

    private void TraceResult(JsonRpcResult.Entry response)
    {
        if (_logger.IsTrace)
        {
            string json = JsonSerializer.Serialize(response, EthereumJsonSerializer.JsonOptionsIndented);

            _logger.Trace($"Sending JSON RPC response: {json}");
        }
    }

    private void TraceResult(JsonRpcErrorResponse response)
    {
        if (_logger.IsTrace)
        {
            string json = JsonSerializer.Serialize(response, EthereumJsonSerializer.JsonOptionsIndented);

            _logger.Trace($"Sending JSON RPC response: {json}");
        }
    }
}
