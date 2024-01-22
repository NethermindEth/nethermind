// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Abstractions;
using System.IO.Pipelines;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
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

    public JsonRpcProcessor(IJsonRpcService jsonRpcService, IJsonRpcConfig jsonRpcConfig, IFileSystem fileSystem, ILogManager logManager)
    {
        _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        ArgumentNullException.ThrowIfNull(fileSystem);

        _jsonRpcService = jsonRpcService ?? throw new ArgumentNullException(nameof(jsonRpcService));
        _jsonRpcConfig = jsonRpcConfig ?? throw new ArgumentNullException(nameof(jsonRpcConfig));

        if (_jsonRpcConfig.RpcRecorderState != RpcRecorderState.None)
        {
            if (_logger.IsWarn) _logger.Warn("Enabling JSON RPC diagnostics recorder - this will affect performance and should be only used in a diagnostics mode.");
            string recorderBaseFilePath = _jsonRpcConfig.RpcRecorderBaseFilePath.GetApplicationResourcePath();
            _recorder = new Recorder(recorderBaseFilePath, fileSystem, _logger);
        }
    }

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

    public async IAsyncEnumerable<JsonRpcResult> ProcessAsync(PipeReader reader, JsonRpcContext context)
    {
        reader = await RecordRequest(reader);
        Stopwatch stopwatch = Stopwatch.StartNew();
        // Initializes a buffer to store the data read from the reader.
        ReadOnlySequence<byte> buffer = default;
        try
        {
            // Continuously read data from the PipeReader in a loop.
            // Can read multiple requests, ends when there is no more requests to read or there is an error in deserialization.
            while (true)
            {
                // Asynchronously reads data from the PipeReader.
                ReadResult readResult = await reader.ReadAsync();
                buffer = readResult.Buffer;
                // Placeholder for a result in case of deserialization failure.
                JsonRpcResult? deserializationFailureResult = null;

                // Processes the buffer while it's not empty; before going out to outer loop to get more data.
                while (!buffer.IsEmpty)
                {
                    JsonDocument? jsonDocument = null;
                    JsonRpcRequest? model = null;
                    ArrayPoolList<JsonRpcRequest>? collection = null;
                    try
                    {
                        // Tries to parse the JSON from the buffer.
                        if (!TryParseJson(ref buffer, out jsonDocument))
                        {
                            // More data needs to be read to complete a document
                            break;
                        }

                        // Deserializes the JSON document into a request object or a collection of requests.
                        (model, collection) = DeserializeObjectOrArray(jsonDocument);
                    }
                    catch (BadHttpRequestException e)
                    {
                        // Increments failure metric and logs the exception, then stops processing.
                        Metrics.JsonRpcRequestDeserializationFailures++;
                        if (_logger.IsDebug) _logger.Debug($"Couldn't read request.{Environment.NewLine}{e}");
                        yield break;
                    }
                    catch (Exception ex)
                    {
                        // Handles general exceptions during parsing and validation.
                        // Sends an error response and stops the stopwatch.
                        Metrics.JsonRpcRequestDeserializationFailures++;
                        if (_logger.IsError) _logger.Error($"Error during parsing/validation.", ex);
                        JsonRpcErrorResponse response = _jsonRpcService.GetErrorResponse(ErrorCodes.ParseError, "Incorrect message");
                        TraceResult(response);
                        stopwatch.Stop();
                        deserializationFailureResult = JsonRpcResult.Single(
                            RecordResponse(response, new RpcReport("# parsing error #", stopwatch.ElapsedMicroseconds(), false)));
                    }

                    // Checks for deserialization failure and yields the result.
                    if (deserializationFailureResult.HasValue)
                    {
                        yield return deserializationFailureResult.Value;
                        break;
                    }
                    else
                    {
                        // Handles a single JSON RPC request.
                        if (model is not null)
                        {
                            if (_logger.IsDebug) _logger.Debug($"JSON RPC request {model}");

                            // Processes the individual request.
                            JsonRpcResult.Entry result = await HandleSingleRequest(model, context);
                            result.Response.AddDisposable(() => jsonDocument.Dispose());

                            // Returns the result of the processed request.
                            yield return JsonRpcResult.Single(RecordResponse(result));
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
                                yield return deserializationFailureResult.Value;
                                break;
                            }

                            // Stops the stopwatch and yields the batch processing result.
                            stopwatch.Stop();
                            yield return JsonRpcResult.Collection(new JsonRpcBatchResult((e, c) => IterateRequest(collection, context, e).GetAsyncEnumerator(c)));
                        }

                        // Handles invalid requests.
                        if (model is null && collection is null)
                        {
                            Metrics.JsonRpcInvalidRequests++;
                            JsonRpcErrorResponse errorResponse = _jsonRpcService.GetErrorResponse(ErrorCodes.InvalidRequest, "Invalid request");
                            errorResponse.AddDisposable(() => jsonDocument.Dispose());

                            TraceResult(errorResponse);
                            stopwatch.Stop();
                            if (_logger.IsDebug) _logger.Debug($"  Failed request handled in {stopwatch.Elapsed.TotalMilliseconds}ms");
                            deserializationFailureResult = JsonRpcResult.Single(RecordResponse(errorResponse, new RpcReport("# parsing error #", stopwatch.ElapsedMicroseconds(), false)));
                            yield return deserializationFailureResult.Value;
                            break;
                        }
                    }
                }

                // Checks if the deserialization failed
                if (deserializationFailureResult.HasValue)
                {
                    break;
                }

                // Checks if the read operation is completed.
                if (readResult.IsCompleted)
                {
                    if (buffer.Length > 0 && (buffer.IsSingleSegment ? buffer.FirstSpan : buffer.ToArray()).IndexOfAnyExcept(WhiteSpace()) >= 0)
                    {
                        Metrics.JsonRpcRequestDeserializationFailures++;
                        if (_logger.IsError) _logger.Error($"Error during parsing/validation. Incomplete request");
                        JsonRpcErrorResponse response = _jsonRpcService.GetErrorResponse(ErrorCodes.ParseError, "Incorrect message");
                        TraceResult(response);
                        stopwatch.Stop();
                        deserializationFailureResult = JsonRpcResult.Single(
                            RecordResponse(response, new RpcReport("# parsing error #", stopwatch.ElapsedMicroseconds(), false)));
                        yield return deserializationFailureResult.Value;
                    }

                    break;
                }

                // Advances the reader to the next segment of the buffer.
                reader.AdvanceTo(buffer.Start, buffer.End);
                buffer = default;
            }
        }
        finally
        {
            // Advances the reader to the end of the buffer if not null.
            if (!buffer.FirstSpan.IsNull())
            {
                reader.AdvanceTo(buffer.End);
            }
        }

        // Completes the PipeReader's asynchronous reading operation.
        await reader.CompleteAsync();
    }

    private static ReadOnlySpan<byte> WhiteSpace() => " \n\r\t"u8;

    private async IAsyncEnumerable<JsonRpcResult.Entry> IterateRequest(
        ArrayPoolList<JsonRpcRequest> requests,
        JsonRpcContext context,
        JsonRpcBatchResultAsyncEnumerator enumerator)
    {
        try
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
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

            if (_logger.IsDebug) _logger.Debug($"  {requests.Count} requests handled in {stopwatch.Elapsed.TotalMilliseconds}ms");
        }
        finally
        {
            requests.Dispose();
        }
    }

    private async Task<JsonRpcResult.Entry> HandleSingleRequest(JsonRpcRequest request, JsonRpcContext context)
    {
        Metrics.JsonRpcRequests++;
        Stopwatch stopwatch = Stopwatch.StartNew();

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

        stopwatch.Stop();

        if (_logger.IsDebug) _logger.Debug($"  {request} handled in {stopwatch.Elapsed.TotalMilliseconds}ms");

        JsonRpcResult.Entry result = new(response, new RpcReport(request.Method, stopwatch.ElapsedMicroseconds(), isSuccess));
        TraceResult(result);
        return result;
    }

    private static bool TryParseJson(ref ReadOnlySequence<byte> buffer, out JsonDocument jsonDocument)
    {
        Utf8JsonReader reader = new(buffer, isFinalBlock: false, default);

        if (JsonDocument.TryParseValue(ref reader, out jsonDocument))
        {
            buffer = buffer.Slice(reader.BytesConsumed);
            return true;
        }

        return false;
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
