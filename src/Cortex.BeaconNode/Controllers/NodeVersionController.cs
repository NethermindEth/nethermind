using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Net.Mime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Cortex.BeaconNode.Controllers
{
    // TODO: Move the controllers into an API package (so the host is mostly empty)

    [ApiController]
    [Produces(MediaTypeNames.Application.Json)]
    [Route("node/version")]
    public class NodeVersionController : ControllerBase
    {
        private readonly ILogger _logger;
        private readonly string _versionString;

        public NodeVersionController(ILogger<NodeVersionController> logger, IWebHostEnvironment environment)
        {
            _logger = logger;
            _versionString = BuildVersionString(environment.ApplicationName, environment.EnvironmentName);
        }

        [HttpGet()]
        /// <summary>Get version string of the running beacon node.</summary>
        public ActionResult<string> Version() 
        {
            _logger.LogDebug("Version request");
            return _versionString;
        }

        // TODO: Move to a service, register, and also log/report on startup
        private string BuildVersionString(string applicationName, string environmentName)
        {
            var assembley = typeof(NodeVersionController).Assembly;
            var versionAttribute = assembley.GetCustomAttributes(false).OfType<AssemblyInformationalVersionAttribute>().FirstOrDefault();
            var version = versionAttribute.InformationalVersion;
            var versionString = $"{applicationName}/{version}";
            if (!string.IsNullOrWhiteSpace(environmentName) && environmentName != "Production") 
            {
                versionString += $" ({environmentName})";
            }
            return versionString;   
        }
    }
}