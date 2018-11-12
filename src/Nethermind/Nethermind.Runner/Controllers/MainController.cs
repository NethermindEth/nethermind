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
using Nethermind.Core.Logging;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.DataModel;

namespace Nethermind.Runner.Controllers
{
    [Route("")]
    [ApiController]
    public class MainController : ControllerBase
    {
        private readonly ILogger _logger;
        private readonly IJsonRpcService _jsonRpcService;
        private readonly IJsonSerializer _jsonSerializer;

        public MainController(ILogManager logManager, IJsonRpcService jsonRpcService, IJsonSerializer jsonSerializer)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _jsonRpcService = jsonRpcService ?? throw new ArgumentNullException(nameof(jsonRpcService));
            _jsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));
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
                if(_logger.IsTrace) _logger.Trace($"Received request: {body}");


                (JsonRpcRequest Model, IEnumerable<JsonRpcRequest> Collection) rpcRequest;
                try
                {
                    rpcRequest = _jsonSerializer.DeserializeObjectOrArray<JsonRpcRequest>(body);
                }
                catch (Exception ex)
                {
                    if(_logger.IsError) _logger.Error($"Error during parsing/validation, request: {body}", ex);
                    var response = _jsonRpcService.GetErrorResponse(ErrorType.ParseError, "Incorrect message");
                    return new JsonResult(response);
                }

                if (rpcRequest.Model != null)
                {
                    return new JsonResult(_jsonRpcService.SendRequest(rpcRequest.Model));
                }

                if (rpcRequest.Collection != null)
                {
                    List<JsonRpcResponse> responses = new List<JsonRpcResponse>();
                    foreach (JsonRpcRequest jsonRpcRequest in rpcRequest.Collection)
                    {
                        responses.Add(_jsonRpcService.SendRequest(jsonRpcRequest));
                    }

                    return new JsonResult(responses);
                }

                {
                    var response = _jsonRpcService.GetErrorResponse(ErrorType.InvalidRequest, "Incorrect request");
                    return new JsonResult(response);    
                }
            }
        }
    }
}