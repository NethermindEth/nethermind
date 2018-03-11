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

        //// GET api/values
        //[HttpGet]
        //public IEnumerable<string> Get()
        //{
        //    return new string[] { "value1", "value2" };
        //}

        /**
         * POST method for RPC impl, receives Json and returns Json
         */
        //[HttpPost]
        //public string Post([FromBody]string value)
        //{
        //    _logger.Log($"Received request: {value}");
        //    var response = _jsonRpcService.SendRequest(value);
        //    _logger.Log($"Returning response: {response}");
        //    return response;
        //}

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
