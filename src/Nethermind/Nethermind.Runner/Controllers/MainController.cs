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

using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Nethermind.JsonRpc;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Nethermind.Runner.Controllers
{
    [Route("")]
    [ApiController]
    public class MainController : ControllerBase
    {

        private readonly IJsonRpcProcessor _jsonRpcProcessor;
        private readonly JsonSerializerSettings _jsonSettings;

        public MainController(IJsonRpcProcessor jsonRpcProcessor, IJsonRpcService jsonRpcService)
        {
            _jsonRpcProcessor = jsonRpcProcessor;
            _jsonSettings = new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver()
            };
            foreach (var converter in jsonRpcService.Converters)
            {
                _jsonSettings.Converters.Add(converter);
            }
        }

        [HttpGet]
        public ActionResult<string> Get() => "Nethermind JSON RPC";

        [HttpPost]
        public async Task<JsonResult> Post()
        {
            using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
            {
                var result = await _jsonRpcProcessor.ProcessAsync(await reader.ReadToEndAsync());
                return result.IsCollection
                    ? new JsonResult(result.Responses, _jsonSettings)
                    : new JsonResult(result.Responses.SingleOrDefault(), _jsonSettings);
            }
        }
    }
}