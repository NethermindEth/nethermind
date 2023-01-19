// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    [Route("node/syncing")]
    public class NodeSyncingController : ControllerBase
    {
        private readonly IBeaconNodeApi _beaconNode;
        private readonly ILogger _logger;

        public NodeSyncingController(ILogger<NodeSyncingController> logger, IBeaconNodeApi beaconNode)
        {
            _logger = logger;
            _beaconNode = beaconNode;
        }

        /// <summary>Poll to see if the beacon node is syncing.</summary>
        /// <remarks>
        /// <para>
        /// Requests the beacon node to describe if it's currently syncing or not, and if it is, what block it is up to. This is modelled after the Eth1.0 JSON-RPC eth_syncing call.
        /// </para>
        /// </remarks>
        [HttpGet]
        public async Task<IActionResult> GetAsync(CancellationToken cancellationToken)
        {
            if (_logger.IsDebug()) LogDebug.NodeSyncingRequested(_logger, null);

            ApiResponse<Syncing> apiResponse =
                await _beaconNode.GetSyncingAsync(cancellationToken).ConfigureAwait(false);
            if (apiResponse.StatusCode == Core2.Api.StatusCode.Success)
            {
                return Ok(apiResponse.Content);
            }

            return Problem("Beacon node internal error.", statusCode: (int)apiResponse.StatusCode);
        }
    }
}
