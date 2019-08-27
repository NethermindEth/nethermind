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
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Nethermind.JsonRpc;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Nethermind.Runner.Controllers
{
    [Route("{*url}")]
    [ApiController]
    public class MainController : ControllerBase
    {
        private readonly IJsonRpcProcessor _jsonRpcProcessor;
        private static JsonSerializerSettings _jsonSettings;
        private static JsonSerializer _serializer;
        private static object _lockObject = new object();

        public MainController(IJsonRpcProcessor jsonRpcProcessor, IJsonRpcService jsonRpcService)
        {
            _jsonRpcProcessor = jsonRpcProcessor;

            if (_serializer == null)
            {
                lock (_lockObject)
                {
                    if (_serializer == null)
                    {
                        _jsonSettings = new JsonSerializerSettings
                        {
                            ContractResolver = new CamelCasePropertyNamesContractResolver()
                        };

                        foreach (var converter in jsonRpcService.Converters)
                        {
                            _jsonSettings.Converters.Add(converter);
                        }

                        _serializer = JsonSerializer.Create(_jsonSettings);
                    }
                }
            }
        }

        [HttpGet]
        public ActionResult<string> Get() => "Nethermind JSON RPC";

        [HttpPost]
        public async Task Post()
        {
            using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
            {
                string request = await reader.ReadToEndAsync();
                var result = await _jsonRpcProcessor.ProcessAsync(request);
                using (var streamWriter = new StreamWriter(Response.Body))
                using (var jsonTextWriter = new JsonTextWriter(streamWriter))
                {
                    if (result.IsCollection)
                    {
                        _serializer.Serialize(jsonTextWriter, result.Responses);
                    }
                    else
                    {
                        _serializer.Serialize(jsonTextWriter, result.Responses[0]);
                    }
                }
            }
        }
    }
}
