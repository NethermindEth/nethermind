// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Abstractions;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc
{
    public class JsonRpcProcessor : IJsonRpcProcessor
    {
        private readonly IJsonRpcConfig _jsonRpcConfig;
        private readonly ILogger _logger;
        private readonly IJsonRpcService _jsonRpcService;
        private readonly Recorder _recorder;

        public JsonRpcProcessor(IJsonRpcService jsonRpcService, IJsonRpcConfig jsonRpcConfig, IFileSystem fileSystem, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            if (fileSystem is null) throw new ArgumentNullException(nameof(fileSystem));

            _jsonRpcService = jsonRpcService ?? throw new ArgumentNullException(nameof(jsonRpcService));
            _jsonRpcConfig = jsonRpcConfig ?? throw new ArgumentNullException(nameof(jsonRpcConfig));

            if (_jsonRpcConfig.RpcRecorderState != RpcRecorderState.None)
            {
                if (_logger.IsWarn) _logger.Warn("Enabling JSON RPC diagnostics recorder - this will affect performance and should be only used in a diagnostics mode.");
                string recorderBaseFilePath = _jsonRpcConfig.RpcRecorderBaseFilePath.GetApplicationResourcePath();
                _recorder = new Recorder(recorderBaseFilePath, fileSystem, _logger);
            }
        }

        private async ValueTask<(JsonRpcRequest? Model, List<JsonRpcRequest>? Collection, IDisposable disposable)> DeserializeObjectOrArray(Stream stream)
        {
            JsonDocument doc = await JsonDocument.ParseAsync(stream);

            JsonValueKind type = doc.RootElement.ValueKind;
            if (type == JsonValueKind.Array)
            {
                return (null, DeserializeArray(doc.RootElement), doc);
            }
            else if (type == JsonValueKind.Object)
            {
                return (DeserializeObject(doc.RootElement), null, doc);
            }
            else
            {
                doc.Dispose();
                throw new Exception("Invalid");
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
                    else if (BigInteger.TryParse(idElement.GetRawText(), out var value))
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

            JsonElement paramsElement = default;
            if (!element.TryGetProperty("params"u8, out paramsElement))
            {
                paramsElement = default;
            }

            return new JsonRpcRequest
            {
                JsonRpc = jsonRpc,
                Id = id,
                Method = method,
                Params = paramsElement
            };
        }

        private List<JsonRpcRequest> DeserializeArray(JsonElement element)
        {
            List<JsonRpcRequest> requests = new();
            foreach (var item in element.EnumerateArray())
            {
                requests.Add(DeserializeObject(item));
            }
            return requests;
        }

        public async IAsyncEnumerable<(JsonRpcResult, IDisposable)> ProcessAsync(Stream stream, JsonRpcContext context)
        {
            stream = await RecordRequest(stream);
            Stopwatch stopwatch = Stopwatch.StartNew();

            JsonRpcRequest? model = null;
            List<JsonRpcRequest>? collection = null;
            IDisposable disposable = null;
            JsonRpcResult? deserializationFailureResult = null;
            try
            {
                (model, collection, disposable) = await DeserializeObjectOrArray(stream);
            }
            catch (BadHttpRequestException e)
            {
                Metrics.JsonRpcRequestDeserializationFailures++;
                if (_logger.IsDebug) _logger.Debug($"Couldn't read request.{Environment.NewLine}{e}");
                yield break;
            }
            catch (Exception ex)
            {
                Metrics.JsonRpcRequestDeserializationFailures++;
                if (_logger.IsError) _logger.Error($"Error during parsing/validation.", ex);
                JsonRpcErrorResponse response = _jsonRpcService.GetErrorResponse(ErrorCodes.ParseError, "Incorrect message");
                TraceResult(response);
                stopwatch.Stop();
                deserializationFailureResult = JsonRpcResult.Single(
                    RecordResponse(response, new RpcReport("# parsing error #", stopwatch.ElapsedMicroseconds(), false)));
            }

            if (deserializationFailureResult.HasValue)
            {
                yield return (deserializationFailureResult.Value, disposable);
            }
            else
            {
                if (model is not null)
                {
                    if (_logger.IsDebug) _logger.Debug($"JSON RPC request {model}");

                    JsonRpcResult.Entry result = await HandleSingleRequest(model, context);

                    yield return (JsonRpcResult.Single(RecordResponse(result)), disposable);
                }

                if (collection is not null)
                {
                    if (_logger.IsDebug) _logger.Debug($"{collection.Count} JSON RPC requests");

                    if (!context.IsAuthenticated && collection.Count > _jsonRpcConfig.MaxBatchSize)
                    {
                        if (_logger.IsWarn) _logger.Warn($"The batch size limit was exceeded. The requested batch size {collection.Count}, and the current config setting is JsonRpc.{nameof(_jsonRpcConfig.MaxBatchSize)} = {_jsonRpcConfig.MaxBatchSize}.");
                        JsonRpcErrorResponse? response = _jsonRpcService.GetErrorResponse(ErrorCodes.LimitExceeded, "Batch size limit exceeded");

                        yield return (JsonRpcResult.Single(RecordResponse(response, RpcReport.Error)), disposable);
                    }

                    stopwatch.Stop();
                    yield return (JsonRpcResult.Collection(new JsonRpcBatchResult((e, c) => IterateRequest(collection, context, e).GetAsyncEnumerator(c))), disposable);
                }

                if (model is null && collection is null)
                {
                    Metrics.JsonRpcInvalidRequests++;
                    JsonRpcErrorResponse errorResponse = _jsonRpcService.GetErrorResponse(ErrorCodes.InvalidRequest, "Invalid request");
                    TraceResult(errorResponse);
                    stopwatch.Stop();
                    if (_logger.IsDebug) _logger.Debug($"  Failed request handled in {stopwatch.Elapsed.TotalMilliseconds}ms");
                    yield return (JsonRpcResult.Single(RecordResponse(errorResponse, new RpcReport("# parsing error #", stopwatch.ElapsedMicroseconds(), false))), disposable);
                }
            }
        }

        private async IAsyncEnumerable<JsonRpcResult.Entry> IterateRequest(
            List<JsonRpcRequest> requests,
            JsonRpcContext context,
            JsonRpcBatchResultAsyncEnumerator enumerator)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();

            int requestIndex = 0;
            for (int index = 0; index < requests.Count; index++)
            {
                JsonRpcRequest jsonRpcRequest = requests[index];

                JsonRpcResult.Entry response = enumerator.IsStopped
                    ? new JsonRpcResult.Entry(
                        _jsonRpcService.GetErrorResponse(
                            jsonRpcRequest.Method,
                            ErrorCodes.LimitExceeded,
                            $"{nameof(IJsonRpcConfig.MaxBatchResponseBodySize)} of {_jsonRpcConfig.MaxBatchResponseBodySize / 1.KB()}KB exceeded",
                            jsonRpcRequest.Id),
                        RpcReport.Error)
                    : await HandleSingleRequest(jsonRpcRequest, context);

                if (_logger.IsDebug) _logger.Debug($"  {++requestIndex}/{requests.Count} JSON RPC request - {jsonRpcRequest} handled after {response.Report.HandlingTimeMicroseconds}");
                TraceResult(response);
                yield return RecordResponse(response);
            }

            if (_logger.IsDebug) _logger.Debug($"  {requests.Count} requests handled in {stopwatch.Elapsed.TotalMilliseconds}ms");
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
                if (_logger.IsWarn) _logger.Warn($"Error when handling {request} | {JsonSerializer.Serialize(localErrorResponse, EthereumJsonSerializer.JsonOptionsIndented)}");
                Metrics.JsonRpcErrors++;
            }
            else
            {
                if (_logger.IsDebug) _logger.Debug($"Responded to {request}");
                Metrics.JsonRpcSuccesses++;
            }

            stopwatch.Stop();
            if (_logger.IsDebug) _logger.Debug($"  {request} handled in {stopwatch.Elapsed.TotalMilliseconds}ms");

            if (_logger.IsDebug) _logger.Debug($"  {request} handled in {stopwatch.Elapsed.TotalMilliseconds}ms");
            JsonRpcResult.Entry result = new(response, new RpcReport(request.Method, stopwatch.ElapsedMicroseconds(), isSuccess));
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

        private async ValueTask<Stream> RecordRequest(Stream stream)
        {
            if ((_jsonRpcConfig.RpcRecorderState & RpcRecorderState.Request) != 0)
            {
                Stream memoryStream = new MemoryStream();
                stream.CopyTo(memoryStream);
                memoryStream.Seek(0, SeekOrigin.Begin);

                StreamReader reader = new(memoryStream);

                string requestString = await reader.ReadToEndAsync();
                _recorder.RecordRequest(requestString);

                memoryStream.Seek(0, SeekOrigin.Begin);
                return memoryStream;
            }

            return stream;
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
}
