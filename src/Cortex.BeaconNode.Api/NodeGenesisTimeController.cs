using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Net.Mime;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Cortex.BeaconNode;

namespace Cortex.BeaconNode.Api
{
    [ApiController]
    [Produces(MediaTypeNames.Application.Json)]
    [Route("node/genesis_time")]
    public class NodeGenesisTimeController : ControllerBase
    {
        private readonly BeaconChain _beaconChain;
        private readonly ILogger _logger;

        public NodeGenesisTimeController(ILogger<NodeVersionController> logger, BeaconChain beaconChain)
        {
            _logger = logger;
            _beaconChain = beaconChain;
        }

        [HttpGet()]
        /// <summary>Get version string of the running beacon node.</summary>
        public ActionResult<ulong> Get() 
        {
            _logger.LogDebug("Genesis Time request");
            var genesisTime = _beaconChain.State.GenesisTime;
            return genesisTime;
        }

    }
}