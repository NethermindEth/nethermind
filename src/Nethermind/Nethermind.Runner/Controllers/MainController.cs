/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Nethermind.Core;
using Nethermind.Core.Json;
using Nethermind.Core.Logging;
using Nethermind.JsonRpc;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Nethermind.Runner.Controllers
{
    [Route("")]
    [ApiController]
    public class MainController : ControllerBase
    {
        private readonly ILogger _logger;
        private readonly IJsonRpcService _jsonRpcService;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly JsonSerializer _traceSerializer;
        private readonly JsonSerializerSettings _jsonSettings;

        public MainController(ILogManager logManager, IJsonRpcService jsonRpcService, IJsonSerializer jsonSerializer)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _jsonRpcService = jsonRpcService ?? throw new ArgumentNullException(nameof(jsonRpcService));
            _jsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));            
            _jsonSettings = new JsonSerializerSettings();
            _jsonSettings.ContractResolver = new CamelCasePropertyNamesContractResolver();
            for (int i = 0; i < _jsonRpcService.Converters.Count; i++)
            {
                _jsonSettings.Converters.Add(_jsonRpcService.Converters[i]);
            }
            
            _traceSerializer = JsonSerializer.Create(_jsonSettings);
        }

        [HttpGet]
        public ActionResult<string> Get()
        {
            return "Test successfull";
        }

        [HttpPost]
        public async Task<JsonResult> Post()
        {
            using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
            {
                var body = await reader.ReadToEndAsync();
                if (_logger.IsTrace) _logger.Trace($"Received JSON RPC request: {body}");
                
                (JsonRpcRequest Model, IEnumerable<JsonRpcRequest> Collection) rpcRequest;
                try
                {
                    rpcRequest = _jsonSerializer.DeserializeObjectOrArray<JsonRpcRequest>(body);
                }
                catch (Exception ex)
                {
                    Metrics.JsonRpcRequestDeserializationFailures++;
                    if (_logger.IsError) _logger.Error($"Error during parsing/validation, request: {body}", ex);
                    var response = _jsonRpcService.GetErrorResponse(ErrorType.ParseError, "Incorrect message");
                    return BuildResult(response);
                }

                if (rpcRequest.Model != null)
                {
                    Metrics.JsonRpcRequests++;
                    var response = _jsonRpcService.SendRequest(rpcRequest.Model);
                    if (response.Error != null)
                    {
                        if (_logger.IsError) _logger.Error($"Failed to respond to {rpcRequest.Model.Method} {response.Error.Message}");
                        Metrics.JsonRpcErrors++;   
                    }
                    else
                    {
                        if (_logger.IsDebug) _logger.Debug($"Responded to {rpcRequest.Model.Method}");
                        Metrics.JsonRpcSuccesses++;
                    }
                    
                    return BuildResult(response);
                }

                if (rpcRequest.Collection != null)
                {
                    List<JsonRpcResponse> responses = new List<JsonRpcResponse>();
                    foreach (JsonRpcRequest jsonRpcRequest in rpcRequest.Collection)
                    {
                        Metrics.JsonRpcRequests++;
                        JsonRpcResponse response = _jsonRpcService.SendRequest(jsonRpcRequest);
                        if (response.Error != null)
                        {
                            if (_logger.IsError) _logger.Error($"Failed to respond to {jsonRpcRequest.Method} {response.Error.Message}");
                            Metrics.JsonRpcErrors++; 
                        }
                        else
                        {
                            if (_logger.IsDebug) _logger.Debug($"Responded to {jsonRpcRequest.Method}");
                            Metrics.JsonRpcSuccesses++;
                        }
                        
                        responses.Add(response);
                    }

                    return BuildResult(responses);
                }

                {
                    Metrics.JsonRpcInvalidRequests++;
                    var response = _jsonRpcService.GetErrorResponse(ErrorType.InvalidRequest, "Invalid request");
                    return BuildResult(response);
                }
            }
        }

        private JsonResult BuildResult(List<JsonRpcResponse> responses)
        {
            if (_logger.IsTrace)
            {
                TraceResult(responses.ToArray());
            }
            
            return new JsonResult(responses, _jsonSettings);
        }
        
        private JsonResult BuildResult(JsonRpcResponse response)
        {
            if (_logger.IsTrace)
            {
                TraceResult(response);
            }
            
            return new JsonResult(response, _jsonSettings);
        }
        
        private void TraceResult(params JsonRpcResponse[] responses)
        {
            StringBuilder builder = new StringBuilder();
            using (StringWriter stringWriter = new StringWriter(builder))
            using (var jsonWriter = new JsonTextWriter(stringWriter))
            {
                _traceSerializer.Serialize(jsonWriter, responses);
            }

            if (_logger.IsTrace)
            {
                _logger.Trace($"Sending JSON RPC response: {builder}");
            }
        }
    }
}