//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Nethermind.Core2;
using Nethermind.Core2.Api;
using Nethermind.Logging.Microsoft;

namespace Nethermind.BeaconNode.OApi.Controllers
{
    [ApiController]
    [Route("node/version")]
    public class NodeVersionController : ControllerBase
    {
        private readonly IBeaconNodeApi _beaconNode;
        private readonly ILogger _logger;

        public NodeVersionController(ILogger<NodeVersionController> logger, IBeaconNodeApi beaconNode)
        {
            _logger = logger;
            _beaconNode = beaconNode;
        }

        /// <summary>Get version string of the running beacon node.</summary>
        /// <remarks>
        /// <para>
        /// Requests that the beacon node identify information about its implementation in a format similar to a  [HTTP User-Agent](https://tools.ietf.org/html/rfc7231#section-5.5.3) field.
        /// </para>
        /// </remarks>
        [HttpGet]
        public async Task<IActionResult> GetAsync(CancellationToken cancellationToken)
        {
            if (_logger.IsInfo()) Log.NodeVersionRequested(_logger, HttpContext.Connection.RemoteIpAddress, null);

            ApiResponse<string> apiResponse =
                await _beaconNode.GetNodeVersionAsync(cancellationToken).ConfigureAwait(false);
            if (apiResponse.StatusCode == Core2.Api.StatusCode.Success)
            {
                return Ok(apiResponse.Content);
            }

            return Problem("Beacon node internal error.", statusCode: (int) apiResponse.StatusCode);
        }
    }
}