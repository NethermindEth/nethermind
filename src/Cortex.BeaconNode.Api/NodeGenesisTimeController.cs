using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Net.Mime;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Cortex.BeaconNode.Api
{
    [ApiController]
    [Produces(MediaTypeNames.Application.Json)]
    [Route("node/genesis_time")]
    public class NodeGenesisTimeController : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger _logger;

        public NodeGenesisTimeController(ILogger<NodeVersionController> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
        }

        [HttpGet()]
        /// <summary>Get version string of the running beacon node.</summary>
        public ActionResult<UInt64> Get() 
        {
            _logger.LogDebug("Genesis Time request");
            var minTime = _configuration.GetValue<UInt64>("MIN_GENESIS_TIME");
            return minTime;
        }

    }
}