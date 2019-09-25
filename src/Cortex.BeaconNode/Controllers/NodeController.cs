using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Cortex.BeaconNode.Controllers
{
    [Produces(MediaTypeNames.Application.Json)]
    [Route("[controller]")]
    public class NodeController : ControllerBase
    {
        // TODO: Get this from the informational version
        string _version = "Cortex/0.1.0";

        public NodeController()
        {
        }

        [HttpGet("version")]
        /// <summary>Get version string of the running beacon node.</summary>
        public ActionResult<string> Version() 
        {
            return _version;
        }
    }
}