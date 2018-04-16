using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Nethermind.Core;
using Nethermind.JsonRpc;

namespace Nethermind.Runner.Controllers
{
    [Route("")]
    public class MainController : Controller
    {
        private readonly ILogger _logger;
        private readonly IJsonRpcService _jsonRpcService;

        public MainController(ILogger logger, IJsonRpcService jsonRpcService)
        {
            _logger = logger;
            _jsonRpcService = jsonRpcService;
        }

        [HttpPost]
        public async Task<string> Post()
        {
            using (StreamReader reader = new StreamReader(Request.Body, Encoding.UTF8))
            {
                var value = await reader.ReadToEndAsync();
                _logger.Log($"Received request: {value}");
                var response = _jsonRpcService.SendRequest(value);
                _logger.Log($"Returning response: {response}");
                return response;
            }
        }
    }
}
