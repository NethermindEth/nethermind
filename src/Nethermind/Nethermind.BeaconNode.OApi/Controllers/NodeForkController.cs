// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Nethermind.Core2;
using Nethermind.Core2.Api;
using Nethermind.Core2.Containers;
using Nethermind.Logging.Microsoft;

namespace Nethermind.BeaconNode.OApi.Controllers
{
    [ApiController]
    [Route("node/fork")]
    public class NodeForkController : ControllerBase
    {
        private readonly IBeaconNodeApi _beaconNode;
        private readonly ILogger _logger;

        public NodeForkController(ILogger<NodeForkController> logger, IBeaconNodeApi beaconNode)
        {
            _logger = logger;
            _beaconNode = beaconNode;
        }

        /// <summary>Get fork information from running beacon node.</summary>
        /// <remarks>
        /// <para>
        /// Requests the beacon node to provide which fork version it is currently on.
        /// </para>
        /// </remarks>
        [HttpGet]
        public async Task<IActionResult> GetAsync(CancellationToken cancellationToken)
        {
            if (_logger.IsDebug()) LogDebug.NodeForkRequested(_logger, null);

            ApiResponse<Fork> apiResponse = await _beaconNode.GetNodeForkAsync(cancellationToken).ConfigureAwait(false);
            if (apiResponse.StatusCode == Core2.Api.StatusCode.Success)
            {
                ForkInformation forkInformation = new ForkInformation(0, apiResponse.Content);
                return Ok(forkInformation);
            }

            return Problem("Beacon node internal error.", statusCode: (int)apiResponse.StatusCode);
        }
    }
}
