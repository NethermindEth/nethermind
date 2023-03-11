// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc.Utils;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Nethermind.JsonRpc
{
    public class JsonRpcProcessor : IJsonRpcProcessor
    {
        private JsonSerializer _traceSerializer;
        private readonly IJsonRpcConfig _jsonRpcConfig;
        private readonly ILogger _logger;
        private readonly JsonSerializer _obsoleteBasicJsonSerializer = new();
        private readonly IJsonRpcService _jsonRpcService;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly Recorder _recorder;

        public JsonRpcProcessor(IJsonRpcService jsonRpcService, IJsonSerializer jsonSerializer, IJsonRpcConfig jsonRpcConfig, IFileSystem fileSystem, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            if (fileSystem is null) throw new ArgumentNullException(nameof(fileSystem));

            _jsonRpcService = jsonRpcService ?? throw new ArgumentNullException(nameof(jsonRpcService));
            _jsonRpcConfig = jsonRpcConfig ?? throw new ArgumentNullException(nameof(jsonRpcConfig));
            _jsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));

            if (_jsonRpcConfig.RpcRecorderState != RpcRecorderState.None)
            {
                if (_logger.IsWarn) _logger.Warn("Enabling JSON RPC diagnostics recorder - this will affect performance and should be only used in a diagnostics mode.");
                string recorderBaseFilePath = _jsonRpcConfig.RpcRecorderBaseFilePath.GetApplicationResourcePath();
                _recorder = new Recorder(recorderBaseFilePath, fileSystem, _logger);
            }

            BuildTraceJsonSerializer();
        }

        /// <summary>
        /// The serializer is created in a way that mimics the behaviour of the Kestrel serialization
        /// and can be used for recording and replaying JSON RPC calls.
        /// </summary>
        private void BuildTraceJsonSerializer()
        {
            JsonSerializerSettings jsonSettings = new()
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver()
            };

            foreach (JsonConverter converter in _jsonRpcService.Converters)
            {
                jsonSettings.Converters.Add(converter);
            }

            _traceSerializer = JsonSerializer.Create(jsonSettings);
        }

        private IEnumerable<(JsonRpcRequest Model, List<JsonRpcRequest> Collection)> DeserializeObjectOrArray(TextReader json)
        {
            IEnumerable<JToken> parsedJson = JTokenUtils.ParseMulticontent(json);

            foreach (JToken token in parsedJson)
            {
                if (token is JArray array)
                {
                    foreach (JToken tokenElement in array)
                    {
                        UpdateParams(tokenElement);
                    }

                    yield return (null, array.ToObject<List<JsonRpcRequest>>(_obsoleteBasicJsonSerializer));
                }
                else
                {
                    UpdateParams(token);
                    yield return (token.ToObject<JsonRpcRequest>(_obsoleteBasicJsonSerializer), null);
                }
            }
        }

        private void UpdateParams(JToken token)
        {
            var paramsToken = token.SelectToken("params");
            if (paramsToken is null)
            {
                paramsToken = token.SelectToken("Params");
                if (paramsToken is null)
                {
                    return;
                }
            }

            if (paramsToken is JValue)
            {
                return; // null
            }

            JArray arrayToken = (JArray)paramsToken;
            for (int i = 0; i < arrayToken.Count; i++)
            {
                if (arrayToken[i].Type == JTokenType.Array || arrayToken[i].Type == JTokenType.Object)
                {
                    arrayToken[i].Replace(JToken.Parse(_jsonSerializer.Serialize(arrayToken[i].Value<object>().ToString())));
                }
            }
        }

        public async IAsyncEnumerable<JsonRpcResult> ProcessAsync(TextReader request, JsonRpcContext context)
        {
            request = await RecordRequest(request);
            Stopwatch stopwatch = Stopwatch.StartNew();
            IEnumerable<(JsonRpcRequest Model, List<JsonRpcRequest> Collection)> rpcRequests = DeserializeObjectOrArray(request);

            using IEnumerator<(JsonRpcRequest Model, List<JsonRpcRequest> Collection)> enumerator = rpcRequests.GetEnumerator();

            bool moveNext = true;

            do
            {
                JsonRpcResult? deserializationFailureResult = null;
                try
                {
                    moveNext = enumerator.MoveNext();
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
                    moveNext = false;
                    yield return deserializationFailureResult.Value;
                }
                else if (moveNext)
                {
                    (JsonRpcRequest Model, List<JsonRpcRequest> Collection) rpcRequest = enumerator.Current;

                    if (rpcRequest.Model is not null)
                    {
                        if (_logger.IsDebug) _logger.Debug($"JSON RPC request {rpcRequest.Model}");

                        JsonRpcResult.Entry result = await HandleSingleRequest(rpcRequest.Model, context);

                        yield return JsonRpcResult.Single(RecordResponse(result));
                    }

                    if (rpcRequest.Collection is not null)
                    {
                        if (_logger.IsDebug) _logger.Debug($"{rpcRequest.Collection.Count} JSON RPC requests");

                        if (!context.IsAuthenticated && rpcRequest.Collection.Count > _jsonRpcConfig.MaxBatchSize)
                        {
                            if (_logger.IsWarn) _logger.Warn($"The batch size limit was exceeded. The requested batch size {rpcRequest.Collection.Count}, and the current config setting is JsonRpc.{nameof(_jsonRpcConfig.MaxBatchSize)} = {_jsonRpcConfig.MaxBatchSize}.");
                            JsonRpcErrorResponse? response = _jsonRpcService.GetErrorResponse(ErrorCodes.LimitExceeded, "Batch size limit exceeded");

                            yield return JsonRpcResult.Single(RecordResponse(response, RpcReport.Error));
                            continue;
                        }

                        stopwatch.Stop();
                        yield return JsonRpcResult.Collection(new JsonRpcBatchResult((e, c) => IterateRequest(rpcRequest.Collection, context, e).GetAsyncEnumerator(c)));
                    }

                    if (rpcRequest.Model is null && rpcRequest.Collection is null)
                    {
                        Metrics.JsonRpcInvalidRequests++;
                        JsonRpcErrorResponse errorResponse = _jsonRpcService.GetErrorResponse(ErrorCodes.InvalidRequest, "Invalid request");
                        TraceResult(errorResponse);
                        stopwatch.Stop();
                        if (_logger.IsDebug) _logger.Debug($"  Failed request handled in {stopwatch.Elapsed.TotalMilliseconds}ms");
                        yield return JsonRpcResult.Single(RecordResponse(errorResponse, new RpcReport("# parsing error #", stopwatch.ElapsedMicroseconds(), false)));
                    }
                }
            } while (moveNext);
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
                if (_logger.IsWarn) _logger.Warn($"Error when handling {request} | {_jsonSerializer.Serialize(localErrorResponse)}");
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
                _recorder.RecordResponse(_jsonSerializer.Serialize(result));
            }

            return result;
        }

        private async ValueTask<TextReader> RecordRequest(TextReader request)
        {
            if ((_jsonRpcConfig.RpcRecorderState & RpcRecorderState.Request) != 0)
            {
                string requestString = await request.ReadToEndAsync();
                _recorder.RecordRequest(requestString);
                return new StringReader(requestString);
            }

            return request;
        }

        private void TraceResult(JsonRpcResult.Entry response)
        {
            if (_logger.IsTrace)
            {
                StringBuilder builder = new();
                using StringWriter stringWriter = new(builder);
                using JsonTextWriter jsonWriter = new(stringWriter);
                _traceSerializer.Serialize(jsonWriter, response);

                _logger.Trace($"Sending JSON RPC response: {builder}");
            }
        }

        private void TraceResult(JsonRpcErrorResponse response)
        {
            if (_logger.IsTrace)
            {
                StringBuilder builder = new();
                using StringWriter stringWriter = new(builder);
                using JsonTextWriter jsonWriter = new(stringWriter);
                _traceSerializer.Serialize(jsonWriter, response);

                _logger.Trace($"Sending JSON RPC response: {builder}");
            }
        }
    }
}
