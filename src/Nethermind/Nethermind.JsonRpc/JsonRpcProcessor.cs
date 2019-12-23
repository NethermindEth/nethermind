//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Nethermind.JsonRpc
{
    public class JsonRpcProcessor : IJsonRpcProcessor
    {
        private IJsonRpcService _jsonRpcService;
        private IJsonSerializer _jsonSerializer;
        private JsonSerializer _traceSerializer;
        private IJsonRpcConfig _jsonRpcConfig;
        private ILogger _logger;

        private Recorder _recorder;

        public JsonRpcProcessor(IJsonRpcService jsonRpcService, IJsonSerializer jsonSerializer, IJsonRpcConfig jsonRpcConfig, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _jsonRpcConfig = jsonRpcConfig ?? throw new ArgumentNullException(nameof(jsonRpcConfig));
            _jsonRpcService = jsonRpcService ?? throw new ArgumentNullException(nameof(jsonRpcService));
            _jsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));

            if (_jsonRpcConfig.RpcRecorderEnabled)
            {
                if (_logger.IsWarn) _logger.Warn("Enabling JSON RPC diagnostics recorder - this will affect performance and should be only used in a diagnostics mode.");
                string recorderBaseFilePath = _jsonRpcConfig.RpcRecorderBaseFilePath.GetApplicationResourcePath();
                _recorder = new Recorder(recorderBaseFilePath, _logger);
            }

            BuildTraceJsonSerializer();
        }

        /// <summary>
        /// The serializer is created in a way that mimics the behaviour of the Kestrel serialization
        /// and can be used for recording and replaying JSON RPC calls.
        /// </summary>
        private void BuildTraceJsonSerializer()
        {
            JsonSerializerSettings jsonSettings = new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver()
            };

            foreach (JsonConverter converter in _jsonRpcService.Converters)
            {
                jsonSettings.Converters.Add(converter);
            }

            _traceSerializer = JsonSerializer.Create(jsonSettings);
        }

        private JsonSerializer _obsoleteBasicJsonSerializer = new JsonSerializer();

        private (JsonRpcRequest Model, List<JsonRpcRequest> Collection) DeserializeObjectOrArray(string json)
        {
            var token = JToken.Parse(json);
            if (token is JArray array)
            {
                foreach (var tokenElement in array)
                {
                    UpdateParams(tokenElement);
                }

                return (default, array.ToObject<List<JsonRpcRequest>>(_obsoleteBasicJsonSerializer));
            }

            UpdateParams(token);
            return (token.ToObject<JsonRpcRequest>(_obsoleteBasicJsonSerializer), null);
        }

        private void UpdateParams(JToken token)
        {
            var paramsToken = token.SelectToken("params");
            if (paramsToken == null)
            {
                paramsToken = token.SelectToken("Params");
                if (paramsToken == null)
                {
                    return;
                }
            }

            if (paramsToken is JValue)
            {
                return; // null
            }

            JArray arrayToken = (JArray) paramsToken;
            for (int i = 0; i < arrayToken.Count; i++)
            {
                if (arrayToken[i].Type == JTokenType.Array || arrayToken[i].Type == JTokenType.Object)
                {
                    arrayToken[i].Replace(JToken.Parse(_jsonSerializer.Serialize(arrayToken[i].Value<object>().ToString())));
                }
            }
        }

        public async Task<JsonRpcResult> ProcessAsync(string request)
        {
            if (_jsonRpcConfig.RpcRecorderEnabled)
            {
                _recorder.RecordRequest(request);
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            (JsonRpcRequest Model, List<JsonRpcRequest> Collection) rpcRequest;
            try
            {
                rpcRequest = DeserializeObjectOrArray(request);
            }
            catch (Exception ex)
            {
                Metrics.JsonRpcRequestDeserializationFailures++;
                if (_logger.IsError) _logger.Error($"Error during parsing/validation, request: {request}", ex);
                JsonRpcResponse response = _jsonRpcService.GetErrorResponse(ErrorCodes.ParseError, "Incorrect message");
                TraceResult(response);
                return JsonRpcResult.Single(response);
            }

            if (rpcRequest.Model != null)
            {
                if (_logger.IsDebug) _logger.Debug($"JSON RPC request {rpcRequest.Model}");

                Metrics.JsonRpcRequests++;
                JsonRpcResponse response = await _jsonRpcService.SendRequestAsync(rpcRequest.Model);
                JsonRpcErrorResponse localErrorResponse = response as JsonRpcErrorResponse;
                if (localErrorResponse != null)
                {
                    if (_logger.IsWarn) _logger.Warn($"Error when handling {rpcRequest.Model} | {_jsonSerializer.Serialize(localErrorResponse)}");
                    Metrics.JsonRpcErrors++;
                }
                else
                {
                    if (_logger.IsDebug) _logger.Debug($"Responded to {rpcRequest.Model}");
                    Metrics.JsonRpcSuccesses++;
                }

                TraceResult(response);
                stopwatch.Stop();
                if (_logger.IsDebug) _logger.Debug($"  {rpcRequest.Model} handled in {stopwatch.Elapsed.TotalMilliseconds}ms");
                return JsonRpcResult.Single(response);
            }

            if (rpcRequest.Collection != null)
            {
                if (_logger.IsDebug) _logger.Debug($"{rpcRequest.Collection.Count} JSON RPC requests");

                var responses = new List<JsonRpcResponse>();
                int requestIndex = 0;
                Stopwatch singleRequestWatch = new Stopwatch();
                foreach (JsonRpcRequest jsonRpcRequest in rpcRequest.Collection)
                {
                    singleRequestWatch.Start();

                    Metrics.JsonRpcRequests++;
                    JsonRpcResponse response = await _jsonRpcService.SendRequestAsync(jsonRpcRequest);
                    JsonRpcErrorResponse localErrorResponse = response as JsonRpcErrorResponse;
                    if (localErrorResponse != null)
                    {
                        if (_logger.IsWarn) _logger.Warn($"Error when handling {jsonRpcRequest} | {_jsonSerializer.Serialize(localErrorResponse)}");
                        Metrics.JsonRpcErrors++;
                    }
                    else
                    {
                        if (_logger.IsDebug) _logger.Debug($"Responded to {jsonRpcRequest}");
                        Metrics.JsonRpcSuccesses++;
                    }

                    singleRequestWatch.Stop();
                    if (_logger.IsDebug) _logger.Debug($"  {requestIndex++}/{rpcRequest.Collection.Count} JSON RPC request - {jsonRpcRequest} handled after {singleRequestWatch.Elapsed.TotalMilliseconds}");
                    responses.Add(response);
                }

                TraceResult(responses);
                stopwatch.Stop();
                if (_logger.IsDebug) _logger.Debug($"  {rpcRequest.Collection.Count} requests handled in {stopwatch.Elapsed.TotalMilliseconds}ms");
                return JsonRpcResult.Collection(responses);
            }

            Metrics.JsonRpcInvalidRequests++;
            JsonRpcErrorResponse errorResponse = _jsonRpcService.GetErrorResponse(ErrorCodes.InvalidRequest, "Invalid request");
            TraceResult(errorResponse);
            stopwatch.Stop();
            if (_logger.IsDebug) _logger.Debug($"  Failed request handled in {stopwatch.Elapsed.TotalMilliseconds}ms");
            return JsonRpcResult.Single(errorResponse);
        }

        private void TraceResult(JsonRpcResponse response)
        {
            if (_logger.IsTrace)
            {
                StringBuilder builder = new StringBuilder();
                using StringWriter stringWriter = new StringWriter(builder);
                using JsonTextWriter jsonWriter = new JsonTextWriter(stringWriter);
                _traceSerializer.Serialize(jsonWriter, response);

                _logger.Trace($"Sending JSON RPC response: {builder}");
            }
        }

        private void TraceResult(List<JsonRpcResponse> responses)
        {
            if (_logger.IsTrace)
            {
                StringBuilder builder = new StringBuilder();
                using StringWriter stringWriter = new StringWriter(builder);
                using JsonTextWriter jsonWriter = new JsonTextWriter(stringWriter);
                _traceSerializer.Serialize(jsonWriter, responses);

                _logger.Trace($"Sending JSON RPC response: {builder}");
            }
        }

        private class Recorder
        {
            private string _recorderBaseFilePath;
            private readonly ILogger _logger;
            private int _recorderFileCounter;
            private string _currentRecorderFilePath;
            private int _currentRecorderFileLength;
            private bool _isEnabled = true;
            private object _recorderSync = new object();

            public Recorder(string basePath, ILogger logger)
            {
                _recorderBaseFilePath = basePath;
                _logger = logger;
                CreateNewRecorderFile();
            }

            private void CreateNewRecorderFile()
            {
                if (!_recorderBaseFilePath.Contains("{counter}"))
                {
                    if(_logger.IsError) _logger.Error("Disabling recorder because of an invalid recorder file path - it should contain '{counter}'");
                    _isEnabled = false;
                    return;
                }

                _currentRecorderFilePath = _recorderBaseFilePath.Replace("{counter}", _recorderFileCounter.ToString());
                File.Create(_currentRecorderFilePath);
                _recorderFileCounter++;
                _currentRecorderFileLength = 0;
            }

            public void RecordRequest(string request)
            {
                if (!_isEnabled)
                {
                    return;
                }

                lock (_recorderSync)
                {
                    _currentRecorderFileLength += request.Length;
                    if (_currentRecorderFileLength > 4 * 1024 * 2014)
                    {
                        CreateNewRecorderFile();
                    }

                    string singleLineRequest = request.Replace(Environment.NewLine, "");
                    File.AppendAllText(_currentRecorderFilePath, singleLineRequest + Environment.NewLine);
                }
            }
        }
    }
}