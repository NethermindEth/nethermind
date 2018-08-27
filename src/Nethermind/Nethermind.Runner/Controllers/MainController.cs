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
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Nethermind.Core.Logging;
using Nethermind.JsonRpc;

namespace Nethermind.Runner.Controllers
{
    [Route("")]
    [ApiController]
    public class MainController : ControllerBase
    {
        private readonly ILogger _logger;
        private readonly IJsonRpcService _jsonRpcService;

        public MainController(ILogManager logManager, IJsonRpcService jsonRpcService)
        {
            _logger = logManager.GetClassLogger();
            _jsonRpcService = jsonRpcService;
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
                var value = await reader.ReadToEndAsync();
                _logger.Info($"Received request: {value}");
                var response = _jsonRpcService.SendRequest(value);
                _logger.Info($"Returning response: {response}");
                return new JsonResult(response);
            }
        }
    }
}
